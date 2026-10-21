param([ValidateSet('Single','Recovery','Economy','Heart','HeartNetwork','Campaign','Advanced','Revival','Network','Resume','Vanilla','All')][string]$Suite='All',[string]$GameRoot='D:\SteamLibrary\steamapps\common\Slay the Spire 2')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$reportRoot=Join-Path $repoRoot 'artifacts\verification'
New-Item -ItemType Directory -Path (Join-Path $reportRoot 'network') -Force | Out-Null
if(Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {throw 'An existing game process is running. Close it before isolated verification.'}
function Start-VerificationProcess([string]$Role,[bool]$Resume,[bool]$Vanilla=$false,[bool]$HeartNetwork=$false) {
  $filename=if($HeartNetwork) {"$Role-heart.log"} elseif($Vanilla) {"$Role-vanilla.log"} elseif($Role -eq 'single') {'single-final.log'} elseif($Resume) {"$Role-resume.log"} else {"$Role.log"}
  $logPath=if($Role -eq 'single') {Join-Path $reportRoot $filename} else {Join-Path $reportRoot "network\$filename"}
  $launch=[Diagnostics.ProcessStartInfo]::new()
  $launch.FileName=Join-Path $GameRoot 'SlayTheSpire2.exe'
  $launch.WorkingDirectory=$GameRoot
  $launch.UseShellExecute=$false
  $launch.CreateNoWindow=$true
  $launch.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
  foreach($argument in @('--headless','--audio-driver','Dummy','--log-file',$logPath,'--max-fps','60','--quit-after','18000')) {$launch.ArgumentList.Add($argument)}
  $launch.Environment['SteamAppId']='2868840'
  $launch.Environment['SteamGameId']='2868840'
  $launch.Environment['FDZF_NATIVE_VERIFY']='1'
  if($Suite -eq 'Recovery') {$launch.Environment['FDZF_VERIFY_RECOVERY']='1'}
  $launch.Environment['FDZF_REPORT_DIR']=Join-Path $reportRoot 'network'
  if($Suite -eq 'Recovery') {
    $recoveryReports=Join-Path $reportRoot 'recovery-suite'
    New-Item -ItemType Directory -Path $recoveryReports -Force | Out-Null
    $launch.Environment['FDZF_REPORT_DIR']=$recoveryReports
  }
  if($HeartNetwork) {$launch.Environment['FDZF_VERIFY_HEART_NETWORK']='1'}
  if($Role -ne 'single') {$launch.Environment['FDZF_VERIFY_ROLE']=$Role}
  if($Resume) {$launch.Environment['FDZF_VERIFY_RESUME']='1'}
  if($Vanilla) {$launch.Environment['FDZF_VERIFY_VANILLA']='1'}
  return [PSCustomObject]@{Process=[Diagnostics.Process]::Start($launch);Log=$logPath;Role=$Role;Resume=$Resume;Vanilla=$Vanilla;HeartNetwork=$HeartNetwork}
}
function Wait-Verification($Records) {
  $deadline=[DateTime]::UtcNow.AddSeconds(300)
  try {
    while(@($Records | Where-Object {!$_.Process.HasExited}).Count -gt 0) {
      if([DateTime]::UtcNow -gt $deadline) {throw 'Native verification timed out.'}
      Start-Sleep -Milliseconds 500
    }
    foreach($record in $Records) {
      $log=Get-Content -LiteralPath $record.Log -Raw
      $marker=if($Suite -eq 'Recovery') {'[FDZF RECOVERY COMPLETE]'} elseif($record.HeartNetwork) {'[FDZF HEART NETWORK COMPLETE]'} elseif($record.Vanilla) {'[FDZF VANILLA COMPLETE]'} elseif($record.Role -eq 'economy') {'[FDZF ECONOMY COMPLETE]'} elseif($record.Role -eq 'heart') {'[FDZF HEART COMPLETE]'} elseif($record.Role -eq 'campaign') {'[FDZF CAMPAIGN COMPLETE]'} elseif($record.Role -eq 'revival') {'[FDZF REVIVAL COMPLETE]'} elseif($record.Role -eq 'advanced') {'[FDZF ADVANCED COMPLETE]'} elseif($record.Role -eq 'single') {'[FDZF VERIFY COMPLETE]'} elseif($record.Resume) {'[FDZF NETWORK RESUME COMPLETE]'} else {'[FDZF NETWORK COMPLETE]'}
      if($record.Process.ExitCode -ne 0 -or !$log.Contains($marker)) {throw "Verification failed: $($record.Log)"}
      if($log -match 'LocException|ObjectDisposedException|turn loop died|\[FDZF (?:VERIFY|NETWORK|ADVANCED|CAMPAIGN|REVIVAL|HEART|ECONOMY) FAILED\]') {throw "Native runtime error: $($record.Log)"}
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

if($Suite -in @('Advanced','All')) {Write-Output 'Starting native advanced effects verification';Wait-Verification @((Start-VerificationProcess 'advanced' $false))}

if($Suite -in @('Campaign','All')) {Write-Output 'Starting native five-act and tier-effects verification';Wait-Verification @((Start-VerificationProcess 'campaign' $false))}

if($Suite -in @('Revival','All')) {Write-Output 'Starting native revival history verification';Wait-Verification @((Start-VerificationProcess 'revival' $false))}

if($Suite -in @('Heart','All')) {Write-Output 'Starting native heart boss verification';Wait-Verification @((Start-VerificationProcess 'heart' $false))}

if($Suite -in @('HeartNetwork','All')) {Write-Output 'Starting two-process heart boss verification';$records=@((Start-VerificationProcess 'host' $false $false $true),(Start-VerificationProcess 'client' $false $false $true));Wait-Verification $records}

if($Suite -in @('Economy','All')) {Write-Output 'Starting native economy and opening-stats verification';Wait-Verification @((Start-VerificationProcess 'economy' $false))}

if($Suite -eq 'Recovery') {
 Write-Output 'Starting host-owned disconnected save verification'
 $records=@((Start-VerificationProcess 'host' $false),(Start-VerificationProcess 'client' $false));Wait-Verification $records
 Write-Output 'Starting recovery from host archive without client resume save'
 $records=@((Start-VerificationProcess 'host' $true),(Start-VerificationProcess 'client' $true));Wait-Verification $records
}
