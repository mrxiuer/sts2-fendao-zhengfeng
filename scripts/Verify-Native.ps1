param([ValidateSet('Single','Network','Resume','Vanilla','All')][string]$Suite='All',[string]$GameRoot='D:\SteamLibrary\steamapps\common\Slay the Spire 2')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$reportRoot=Join-Path $repoRoot 'artifacts\verification'
New-Item -ItemType Directory -Path (Join-Path $reportRoot 'network') -Force | Out-Null
if(Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {throw 'An existing game process is running. Close it before isolated verification.'}
function Start-VerificationProcess([string]$Role,[bool]$Resume,[bool]$Vanilla=$false) {
  $filename=if($Vanilla) {"$Role-vanilla.log"} elseif($Role -eq 'single') {'single-final.log'} elseif($Resume) {"$Role-resume.log"} else {"$Role.log"}
  $logPath=if($Role -eq 'single') {Join-Path $reportRoot $filename} else {Join-Path $reportRoot "network\$filename"}
  $launch=[Diagnostics.ProcessStartInfo]::new()
  $launch.FileName=Join-Path $GameRoot 'SlayTheSpire2.exe'
  $launch.WorkingDirectory=$GameRoot
  $launch.UseShellExecute=$false
  $launch.CreateNoWindow=$true
  $launch.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
  foreach($argument in @('--headless','--audio-driver','Dummy','--log-file',$logPath,'--max-fps','60','--quit-after','7200')) {$launch.ArgumentList.Add($argument)}
  $launch.Environment['SteamAppId']='2868840'
  $launch.Environment['SteamGameId']='2868840'
  $launch.Environment['FDZF_NATIVE_VERIFY']='1'
  $launch.Environment['FDZF_REPORT_DIR']=Join-Path $reportRoot 'network'
  if($Role -ne 'single') {$launch.Environment['FDZF_VERIFY_ROLE']=$Role}
  if($Resume) {$launch.Environment['FDZF_VERIFY_RESUME']='1'}
  if($Vanilla) {$launch.Environment['FDZF_VERIFY_VANILLA']='1'}
  return [PSCustomObject]@{Process=[Diagnostics.Process]::Start($launch);Log=$logPath;Role=$Role;Resume=$Resume;Vanilla=$Vanilla}
}
function Wait-Verification($Records) {
  $deadline=[DateTime]::UtcNow.AddSeconds(115)
  try {
    while(@($Records | Where-Object {!$_.Process.HasExited}).Count -gt 0) {
      if([DateTime]::UtcNow -gt $deadline) {throw 'Native verification timed out.'}
      Start-Sleep -Milliseconds 500
    }
    foreach($record in $Records) {
      $log=Get-Content -LiteralPath $record.Log -Raw
      $marker=if($record.Vanilla) {'[FDZF VANILLA COMPLETE]'} elseif($record.Role -eq 'single') {'[FDZF VERIFY COMPLETE]'} elseif($record.Resume) {'[FDZF NETWORK RESUME COMPLETE]'} else {'[FDZF NETWORK COMPLETE]'}
      if($record.Process.ExitCode -ne 0 -or !$log.Contains($marker)) {throw "Verification failed: $($record.Log)"}
      if($log -match 'LocException|turn loop died|\[FDZF (?:VERIFY|NETWORK) FAILED\]') {throw "Native runtime error: $($record.Log)"}
      Write-Output "PASS $($record.Role) resume=$($record.Resume): $($record.Log)"
    }
  } finally {
    foreach($record in $Records) {if(!$record.Process.HasExited) {$record.Process.Kill()}}
  }
}
if($Suite -in @('Single','All')) {Write-Output 'Starting native single-process verification';Wait-Verification @((Start-VerificationProcess 'single' $false))}
if($Suite -in @('Network','All')) {Write-Output 'Starting native two-process ENet verification';$records=@((Start-VerificationProcess 'host' $false),(Start-VerificationProcess 'client' $false));Wait-Verification $records}
if($Suite -in @('Resume','All')) {Write-Output 'Starting two-process checkpoint restore verification';$records=@((Start-VerificationProcess 'host' $true),(Start-VerificationProcess 'client' $true));Wait-Verification $records}

if($Suite -in @('Vanilla','All')) {Write-Output 'Starting host-off native cooperative verification';$records=@((Start-VerificationProcess 'host' $false $true),(Start-VerificationProcess 'client' $false $true));Wait-Verification $records}
