param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Slay the Spire 2',[string]$OutputDirectory,[switch]$Heart,[switch]$Advanced,[switch]$Campaign,[switch]$Mechanics,[switch]$Gambler,[switch]$PuppetBody,[ValidateSet("PuppetLife","Timeline","Charm","TwinSlime","Reroll","Formation","Combinations")][string]$EffectSuite)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot 'artifacts\visual'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if(Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue){throw 'Close the running game before isolated visual verification.'}
$logPath=Join-Path $OutputDirectory 'visual-verification.log'
$launch=[Diagnostics.ProcessStartInfo]::new()
$launch.FileName=Join-Path $GameRoot 'SlayTheSpire2.exe'
$launch.WorkingDirectory=$GameRoot
$launch.UseShellExecute=$false
$launch.CreateNoWindow=$true
$launch.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
foreach($argument in @('--windowed','--resolution','1440x900','--log-file',$logPath,'--max-fps','60','--quit-after','18000')){$launch.ArgumentList.Add($argument)}
$launch.Environment['SteamAppId']='2868840'
$launch.Environment['SteamGameId']='2868840'
$launch.Environment['FDZF_NATIVE_VERIFY']='1'
$launch.Environment['FDZF_SCREENSHOT_DIR']=$OutputDirectory
if((@($Advanced,$Campaign,$Heart,$Mechanics,$Gambler,$PuppetBody) | Where-Object {$_}).Count -gt 1){throw 'Select only one verification scene suite.'}
if($EffectSuite){$launch.Environment['FDZF_VERIFY_ROLE']=@{Combinations="combinations";Formation="formation";PuppetLife="puppet_life";Timeline="timeline";Charm="charm";TwinSlime="twin_slime";Reroll="reroll"}[$EffectSuite]}
if($PuppetBody){$launch.Environment['FDZF_VERIFY_ROLE']='puppet_body'}
if($Gambler){$launch.Environment['FDZF_VERIFY_ROLE']='gambler'}
if($Mechanics){$launch.Environment['FDZF_VERIFY_ROLE']='mechanics'}
if($Heart){$launch.Environment['FDZF_VERIFY_ROLE']='heart'}
if($Advanced){$launch.Environment['FDZF_VERIFY_ROLE']='advanced'}
if($Campaign){$launch.Environment['FDZF_VERIFY_ROLE']='campaign'}
$process=[Diagnostics.Process]::Start($launch)
try{
    $deadline=[DateTime]::UtcNow.AddSeconds(300)
    while(!$process.HasExited){if([DateTime]::UtcNow -gt $deadline){throw 'Visual verification timed out.'};Start-Sleep -Milliseconds 500}
    $logText=Get-Content -LiteralPath $logPath -Raw
    if($process.ExitCode -ne 0 -or !$logText.Contains($(if($EffectSuite){@{Combinations='[FDZF COMBINATIONS COMPLETE]';Formation='[FDZF FORMATION COMPLETE]';PuppetLife='[FDZF PUPPET LIFE COMPLETE]';Timeline='[FDZF TIMELINE COMPLETE]';Charm='[FDZF CHARM COMPLETE]';TwinSlime='[FDZF TWIN SLIME COMPLETE]';Reroll='[FDZF REROLL COMPLETE]'}[$EffectSuite]}elseif($PuppetBody){'[FDZF PUPPET BODY COMPLETE]'}elseif($Gambler){'[FDZF GAMBLER COMPLETE]'}elseif($Mechanics){'[FDZF MECHANICS COMPLETE]'}elseif($Heart){'[FDZF HEART COMPLETE]'}elseif($Campaign){'[FDZF CAMPAIGN COMPLETE]'}elseif($Advanced){'[FDZF ADVANCED COMPLETE]'}else{'[FDZF VERIFY COMPLETE]'})) -or $logText -match 'LocException|ObjectDisposedException|turn loop died|\[FDZF (?:VERIFY|ADVANCED|CAMPAIGN|HEART) FAILED\]'){throw "Visual verification failed: $logPath"}
    $expected=if($EffectSuite){@{Combinations=@('combination-01-both-buried.png','combination-02-both-dazed.png','combination-03-clean-next-combat.png');Formation=@('formation-01-giant-necrobinder-four-allies.png','formation-02-puppet-resummoned.png');PuppetLife=@('puppet-life-01-guard-block-status.png');Timeline=@('timeline-01-native-card.png');Charm=@('charm-01-native-card-status.png');TwinSlime=@('twin-slime-01-split.png','twin-slime-02-restored.png');Reroll=@('reroll-01-native-reward-buttons.png','reroll-02-native-card-refresh.png','reroll-03-native-card-refreshed.png')}[$EffectSuite]}elseif($PuppetBody){@('puppet-01-native-body.png','puppet-02-native-replacement.png')}elseif($Gambler){@('mechanics-05-gamblers-heart.png','mechanics-05a-critical-popup.png')}elseif($Mechanics){@('mechanics-01-native-orb-and-forge.png','mechanics-02-damage-and-native-status.png','mechanics-03-evil-echo.png','mechanics-04-flight-wings.png','mechanics-05-gamblers-heart.png','mechanics-05a-critical-popup.png')}elseif($Heart){@('heart-01-opening.png','heart-02-copied-deck.png','heart-03-restored-deck.png','heart-04-victory.png')}elseif($Campaign){@('campaign-01-tier-status.png','campaign-02-fourth-act-map.png')}elseif($Advanced){@('advanced-01-card-durability.png','advanced-02-combat-status.png','advanced-03-native-cleanse-tooltip.png','advanced-04-reset-map.png')}else{@('mock-01-clean-opening.png','mock-02-persistent-effect-icons.png','mock-03-effect-test-menu.png')}
    foreach($name in $expected){
        if(!(Test-Path -LiteralPath (Join-Path $OutputDirectory $name))){throw "Missing native screenshot: $name"}
    }
    Write-Output "PASS native visual verification: $OutputDirectory"
}finally{if(!$process.HasExited){$process.Kill()}}
