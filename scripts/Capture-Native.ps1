param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Slay the Spire 2',[string]$OutputDirectory,[switch]$Advanced,[switch]$Campaign)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot 'artifacts\visual'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if(Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue){throw 'Close the running game before isolated visual verification.'}
$logPath=Join-Path $OutputDirectory 'visual-verification.log'
$launch=[Diagnostics.ProcessStartInfo]::new()
$launch.FileName=Join-Path $GameRoot 'SlayTheSpire2.exe'
$launch.WorkingDirectory=$GameRoot
$launch.UseShellExecute=$false
$launch.CreateNoWindow=$true
$launch.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
foreach($argument in @('--windowed','--resolution','1440x900','--log-file',$logPath,'--max-fps','60','--quit-after','7200')){$launch.ArgumentList.Add($argument)}
$launch.Environment['SteamAppId']='2868840'
$launch.Environment['SteamGameId']='2868840'
$launch.Environment['FDZF_NATIVE_VERIFY']='1'
$launch.Environment['FDZF_SCREENSHOT_DIR']=$OutputDirectory
if($Advanced -and $Campaign){throw 'Select Advanced or Campaign, not both.'}
if($Advanced){$launch.Environment['FDZF_VERIFY_ROLE']='advanced'}
if($Campaign){$launch.Environment['FDZF_VERIFY_ROLE']='campaign'}
$process=[Diagnostics.Process]::Start($launch)
try{
    $deadline=[DateTime]::UtcNow.AddSeconds(300)
    while(!$process.HasExited){if([DateTime]::UtcNow -gt $deadline){throw 'Visual verification timed out.'};Start-Sleep -Milliseconds 500}
    $logText=Get-Content -LiteralPath $logPath -Raw
    if($process.ExitCode -ne 0 -or !$logText.Contains($(if($Campaign){'[FDZF CAMPAIGN COMPLETE]'}elseif($Advanced){'[FDZF ADVANCED COMPLETE]'}else{'[FDZF VERIFY COMPLETE]'})) -or $logText -match 'LocException|ObjectDisposedException|turn loop died|\[FDZF (?:VERIFY|ADVANCED|CAMPAIGN) FAILED\]'){throw "Visual verification failed: $logPath"}
    $expected=if($Campaign){@('campaign-01-tier-status.png','campaign-02-fourth-act-map.png')}elseif($Advanced){@('advanced-01-card-durability.png','advanced-02-combat-status.png','advanced-03-native-cleanse-tooltip.png','advanced-04-reset-map.png')}else{@('mock-01-clean-opening.png','mock-02-persistent-effect-icons.png','mock-03-effect-test-menu.png')}
    foreach($name in $expected){
        if(!(Test-Path -LiteralPath (Join-Path $OutputDirectory $name))){throw "Missing native screenshot: $name"}
    }
    Write-Output "PASS native visual verification: $OutputDirectory"
}finally{if(!$process.HasExited){$process.Kill()}}
