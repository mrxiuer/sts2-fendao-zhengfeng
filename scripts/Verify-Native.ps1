param([ValidateSet('Single','Mechanics','Flight','Gambler','PuppetBody','PuppetLife','Giant','Timeline','Charm','Reroll','Burrow','RewardPerformance','RewardNetworkPerformance','RewardChoicePerformance','RewardRefreshPerformance','TwinSlime','Formation','Combinations','NewEffectsNetwork','FourPlayers','ResetNetwork','ServiceRecovery','EventRecovery','MenuResume','Recovery','RewardRecovery','Economy','Heart','HeartNetwork','Campaign','Advanced','Revival','Network','Resume','Vanilla','All')][string]$Suite='All',[string]$GameRoot='D:\SteamLibrary\steamapps\common\Slay the Spire 2',[switch]$ResumeOnly,[switch]$EventGold,[switch]$ResetEmptyMap,[ValidateSet("", "Initial", "Rest")][string]$EventPage="",[switch]$RewardBaseline,[switch]$RewardNoPeriodicBackup,[switch]$PendingClaim,[switch]$SaveFailure,[switch]$GrantInProgress,[switch]$ConfirmedClaim,[switch]$EventInProgress)
$ErrorActionPreference='Stop'
if($ResumeOnly -and $Suite -notin @('Recovery','RewardRecovery','NewEffectsNetwork','FourPlayers','ResetNetwork','ServiceRecovery','EventRecovery','MenuResume')) {throw 'ResumeOnly requires a host-archive recovery suite.'}
if($RewardNoPeriodicBackup -and $Suite -notin @('RewardNetworkPerformance','RewardChoicePerformance','RewardRefreshPerformance')) {throw 'RewardNoPeriodicBackup requires a reward performance suite.'}
$suiteKey=if($Suite -in @('RewardNetworkPerformance','RewardChoicePerformance','RewardRefreshPerformance') -and $RewardBaseline) {"$Suite-baseline"} elseif($Suite -eq 'EventRecovery' -and $EventPage) {"EventRecovery-$EventPage"} elseif($Suite -eq 'EventRecovery' -and $EventGold) {'EventRecovery-Gold'} elseif($Suite -eq 'ResetNetwork' -and $ResetEmptyMap) {'ResetNetwork-EmptyMap'} else {$Suite}
if($RewardNoPeriodicBackup) {$suiteKey += '-no-periodic-backup'}
if($PendingClaim) {if($Suite -ne 'RewardRecovery') {throw 'PendingClaim requires RewardRecovery.'};$suiteKey+='-PendingClaim'}
if($SaveFailure) {if($Suite -ne 'RewardRecovery' -or $PendingClaim) {throw 'SaveFailure requires RewardRecovery without PendingClaim.'};$suiteKey+='-SaveFailure'}
if($GrantInProgress) {if($Suite -ne 'RewardRecovery' -or $PendingClaim -or $SaveFailure) {throw 'GrantInProgress requires RewardRecovery alone.'};$suiteKey+='-GrantInProgress'}
if($ConfirmedClaim) {if($Suite -ne 'RewardRecovery' -or $PendingClaim -or $SaveFailure -or $GrantInProgress) {throw 'ConfirmedClaim requires RewardRecovery alone.'};$suiteKey+='-ConfirmedClaim'}
if($EventInProgress) {if($Suite -ne 'EventRecovery' -or $EventPage) {throw 'EventInProgress requires EventRecovery without EventPage.'};$EventGold=$true;$suiteKey='EventRecovery-InProgress'}
$repoRoot=Split-Path -Parent $PSScriptRoot
$reportRoot=Join-Path $repoRoot 'artifacts\verification'
New-Item -ItemType Directory -Path (Join-Path $reportRoot 'network') -Force | Out-Null
if(@(Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue | Where-Object {!$_.HasExited}).Count -gt 0) {throw 'An existing game process is running. Close it before isolated verification.'}
function Start-VerificationProcess([string]$Role,[bool]$Resume,[bool]$Vanilla=$false,[bool]$HeartNetwork=$false) {
  $filename=if($Suite -in @('RewardNetworkPerformance','RewardChoicePerformance','RewardRefreshPerformance')) {"$Role-$suiteKey.log"} elseif($Suite -in @('Recovery','RewardRecovery','NewEffectsNetwork','FourPlayers','ResetNetwork','ServiceRecovery','EventRecovery','MenuResume')) {if($Resume) {"$Role-$suiteKey-resume.log"} else {"$Role-$suiteKey.log"}} elseif($HeartNetwork) {"$Role-heart.log"} elseif($Vanilla) {"$Role-vanilla.log"} elseif($Role -eq 'single') {'single-final.log'} elseif($Resume) {"$Role-resume.log"} else {"$Role.log"}
  $logPath=if($Role -eq 'single') {Join-Path $reportRoot $filename} else {Join-Path $reportRoot "network\$filename"}
  # The fail-fast observer must never consume a previous run's error.
  [IO.File]::WriteAllText($logPath, '')
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
  if($Suite -in @('RewardNetworkPerformance','RewardChoicePerformance','RewardRefreshPerformance')) {if($Suite -eq 'RewardChoicePerformance') {$launch.Environment['FDZF_VERIFY_REWARD_CHOICES']='1'} elseif($Suite -eq 'RewardRefreshPerformance') {$launch.Environment['FDZF_VERIFY_REWARD_REFRESH']='1'} else {$launch.Environment['FDZF_VERIFY_REWARD_NETWORK']='1'};$launch.Environment['FDZF_PROFILE_REWARDS']='1';if($RewardBaseline) {$launch.Environment['FDZF_REWARD_LEGACY_PATH']='1'}}
  if($RewardNoPeriodicBackup) {$launch.Environment['FDZF_VERIFY_NO_PERIODIC_BACKUP']='1'}
  if($Role -eq 'reward_performance') {$launch.Environment['FDZF_PROFILE_REWARDS']='1'}
  if($Suite -eq 'FourPlayers') {$launch.Environment['FDZF_VERIFY_FOUR_PLAYERS']='1'}
  if($Suite -eq 'ResetNetwork') {$launch.Environment['FDZF_VERIFY_RESET_NETWORK']='1';if($ResetEmptyMap) {$launch.Environment['FDZF_VERIFY_RESET_EMPTY_MAP']='1'}}
  if($Suite -eq 'ServiceRecovery') {$launch.Environment['FDZF_VERIFY_SERVICE_RECOVERY']='1'}
  if($Suite -eq 'EventRecovery') {$launch.Environment['FDZF_VERIFY_EVENT_RECOVERY']='1';if($EventPage) {$launch.Environment['FDZF_VERIFY_EVENT_PAGE']=$EventPage};if($EventGold) {$launch.Environment['FDZF_VERIFY_EVENT_GOLD']='1'}}
  if($Suite -eq 'NewEffectsNetwork') {$launch.Environment['FDZF_VERIFY_NEW_EFFECTS']='1'}
  if($Suite -eq 'Recovery') {$launch.Environment['FDZF_VERIFY_RECOVERY']='1'}
  if($Suite -eq 'MenuResume') {$launch.Environment['FDZF_VERIFY_MENU_RESUME']='1'}
  if($EventInProgress) {$launch.Environment['FDZF_VERIFY_EVENT_IN_PROGRESS']='1'}
  if($ConfirmedClaim) {$launch.Environment['FDZF_VERIFY_CONFIRMED_CLAIM']='1'}
  if($GrantInProgress) {$launch.Environment['FDZF_VERIFY_GRANT_IN_PROGRESS']='1'}
  if($SaveFailure) {$launch.Environment['FDZF_VERIFY_SAVE_FAILURE']='1'}
  if($PendingClaim) {$launch.Environment['FDZF_VERIFY_PENDING_CLAIM']='1'}
  if($Suite -eq 'RewardRecovery') {$launch.Environment['FDZF_VERIFY_REWARD_RECOVERY']='1'}
  $launch.Environment['FDZF_REPORT_DIR']=Join-Path $reportRoot 'network'
  if($Suite -in @('Recovery','RewardRecovery','NewEffectsNetwork','FourPlayers','ResetNetwork','ServiceRecovery','EventRecovery','MenuResume')) {
    $recoveryReports=Join-Path $reportRoot "$suiteKey-suite"
    New-Item -ItemType Directory -Path $recoveryReports -Force | Out-Null
    $launch.Environment['FDZF_REPORT_DIR']=$recoveryReports
  }
  if($Suite -in @('RewardNetworkPerformance','RewardChoicePerformance','RewardRefreshPerformance')) {
    $perfReports=Join-Path $reportRoot $suiteKey
    New-Item -ItemType Directory -Path $perfReports -Force | Out-Null
    $launch.Environment['FDZF_REPORT_DIR']=$perfReports
  }
  if($HeartNetwork) {$launch.Environment['FDZF_VERIFY_HEART_NETWORK']='1'}
  if($Role -ne 'single') {$launch.Environment['FDZF_VERIFY_ROLE']=$Role}
  if($Resume) {$launch.Environment['FDZF_VERIFY_RESUME']='1';$launch.Environment['FDZF_VERIFY_RESUME_FADE']='1'}
  if($Vanilla) {$launch.Environment['FDZF_VERIFY_VANILLA']='1'}
  return [PSCustomObject]@{Process=[Diagnostics.Process]::Start($launch);Log=$logPath;Role=$Role;Resume=$Resume;Vanilla=$Vanilla;HeartNetwork=$HeartNetwork}
}
function Wait-Verification($Records) {
  $deadline=[DateTime]::UtcNow.AddSeconds(300)
  try {
    while(@($Records | Where-Object {!$_.Process.HasExited}).Count -gt 0) {
      if([DateTime]::UtcNow -gt $deadline) {throw 'Native verification timed out.'}
      foreach($record in $Records) {
        if(!$record.Process.HasExited -and (Test-Path -LiteralPath $record.Log)) {
          try {
            $probe=[IO.File]::Open($record.Log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
            $reader=[IO.StreamReader]::new($probe)
            try {$startupLog=$reader.ReadToEnd()} finally {$reader.Dispose()}
          } catch [IO.IOException] {continue}
          if($startupLog.Contains('Exception thrown when calling mod initializer of type FendaoZhengfeng.Mod.ModEntry') -or $startupLog -match 'Failed to initialize Steam!|turn loop died|ObjectDisposedException|LocException|\[FDZF [A-Z ]+ FAILED\]') {
            throw "Native verification failed before completion: $($record.Log)"
          }
        }
      }
      Start-Sleep -Milliseconds 500
    }
    foreach($record in $Records) {
      $log=Get-Content -LiteralPath $record.Log -Raw
      $marker=if($Suite -in @('RewardNetworkPerformance','RewardChoicePerformance','RewardRefreshPerformance')) {'[FDZF REWARD NETWORK COMPLETE]'} elseif($Suite -eq 'EventRecovery') {'[FDZF EVENT RECOVERY COMPLETE]'} elseif($Suite -eq 'ServiceRecovery') {'[FDZF SERVICE RECOVERY COMPLETE]'} elseif($Suite -eq 'ResetNetwork') {'[FDZF RESET NETWORK COMPLETE]'} elseif($Suite -eq 'FourPlayers') {'[FDZF FOUR PLAYERS COMPLETE]'} elseif($Suite -eq 'NewEffectsNetwork') {'[FDZF NEW EFFECTS COMPLETE]'} elseif($Suite -eq 'RewardRecovery') {'[FDZF REWARD RECOVERY COMPLETE]'} elseif($Suite -eq 'Recovery') {'[FDZF RECOVERY COMPLETE]'} elseif($record.HeartNetwork) {'[FDZF HEART NETWORK COMPLETE]'} elseif($record.Vanilla) {'[FDZF VANILLA COMPLETE]'} elseif($record.Role -eq 'combinations') {'[FDZF COMBINATIONS COMPLETE]'} elseif($record.Role -eq 'formation') {'[FDZF FORMATION COMPLETE]'} elseif($record.Role -eq 'twin_slime') {'[FDZF TWIN SLIME COMPLETE]'} elseif($record.Role -eq 'reward_performance') {'[FDZF REWARD PERF COMPLETE]'} elseif($record.Role -eq 'burrow') {'[FDZF BURROW COMPLETE]'} elseif($record.Role -eq 'reroll') {'[FDZF REROLL COMPLETE]'} elseif($record.Role -eq 'charm') {'[FDZF CHARM COMPLETE]'} elseif($record.Role -eq 'timeline') {'[FDZF TIMELINE COMPLETE]'} elseif($record.Role -eq 'giant') {'[FDZF GIANT COMPLETE]'} elseif($record.Role -eq 'puppet_life') {'[FDZF PUPPET LIFE COMPLETE]'} elseif($record.Role -eq 'puppet_body') {'[FDZF PUPPET BODY COMPLETE]'} elseif($record.Role -eq 'gambler') {'[FDZF GAMBLER COMPLETE]'} elseif($record.Role -eq 'flight') {'[FDZF FLIGHT COMPLETE]'} elseif($record.Role -eq 'mechanics') {'[FDZF MECHANICS COMPLETE]'} elseif($record.Role -eq 'economy') {'[FDZF ECONOMY COMPLETE]'} elseif($record.Role -eq 'heart') {'[FDZF HEART COMPLETE]'} elseif($record.Role -eq 'campaign') {'[FDZF CAMPAIGN COMPLETE]'} elseif($record.Role -eq 'revival') {'[FDZF REVIVAL COMPLETE]'} elseif($record.Role -eq 'advanced') {'[FDZF ADVANCED COMPLETE]'} elseif($record.Role -eq 'single') {'[FDZF VERIFY COMPLETE]'} elseif($record.Resume) {'[FDZF NETWORK RESUME COMPLETE]'} else {'[FDZF NETWORK COMPLETE]'}
      if($record.Process.ExitCode -ne 0 -or !$log.Contains($marker)) {throw "Verification failed: $($record.Log)"}
      if($log -match 'LocException|ObjectDisposedException|turn loop died|\[FDZF (?:VERIFY|NETWORK|ADVANCED|CAMPAIGN|REVIVAL|HEART|ECONOMY) FAILED\]') {throw "Native runtime error: $($record.Log)"}
      Write-Output "PASS $($record.Role) resume=$($record.Resume): $($record.Log)"
    }
  } finally {
    foreach($record in $Records) {if(!$record.Process.HasExited) {$record.Process.Kill();[void]$record.Process.WaitForExit(5000)};$record.Process.Dispose()}
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

if($Suite -in @('Recovery','RewardRecovery','NewEffectsNetwork','FourPlayers','ResetNetwork','ServiceRecovery','EventRecovery','MenuResume')) {
 if(!$ResumeOnly) {
  Write-Output 'Starting host-owned disconnected save verification'
  $records=@((Start-VerificationProcess 'host' $false),(Start-VerificationProcess 'client' $false));if($Suite -in @('FourPlayers','ResetNetwork','ServiceRecovery')) {$records+=@((Start-VerificationProcess 'peer3' $false),(Start-VerificationProcess 'peer4' $false))};Wait-Verification $records
 }
 Write-Output 'Starting recovery from host archive without client resume save'
 $records=@((Start-VerificationProcess 'host' $true),(Start-VerificationProcess 'client' $true));if($Suite -in @('FourPlayers','ResetNetwork','ServiceRecovery')) {$records+=@((Start-VerificationProcess 'peer3' $true),(Start-VerificationProcess 'peer4' $true))};Wait-Verification $records
}

if($Suite -eq 'All') {& $PSCommandPath -Suite RewardRecovery -PendingClaim -GameRoot $GameRoot;& $PSCommandPath -Suite RewardRecovery -SaveFailure -GameRoot $GameRoot;& $PSCommandPath -Suite RewardRecovery -GrantInProgress -GameRoot $GameRoot;& $PSCommandPath -Suite RewardRecovery -ConfirmedClaim -GameRoot $GameRoot}

if($Suite -in @('Mechanics','All')) {Write-Output 'Starting native new-item mechanics verification';Wait-Verification @((Start-VerificationProcess 'mechanics' $false))}

if($Suite -eq 'Flight') {Write-Output 'Starting focused native flight verification';Wait-Verification @((Start-VerificationProcess 'flight' $false))}

if($Suite -eq 'Gambler') {Write-Output 'Starting focused native critical verification';Wait-Verification @((Start-VerificationProcess 'gambler' $false))}

if($Suite -in @('PuppetBody','All')) {Write-Output 'Starting focused native follower-body verification';Wait-Verification @((Start-VerificationProcess 'puppet_body' $false))}

if($Suite -in @('Giant','All')) {Write-Output 'Starting focused native giant verification';Wait-Verification @((Start-VerificationProcess 'giant' $false))}

if($Suite -in @('Timeline','All')) {Write-Output 'Starting focused native timeline verification';Wait-Verification @((Start-VerificationProcess 'timeline' $false))}

if($Suite -in @('PuppetLife','All')) {Write-Output 'Starting native puppet lifecycle verification';Wait-Verification @((Start-VerificationProcess 'puppet_life' $false))}

if($Suite -in @('Charm','All')) {Write-Output 'Starting native charm verification';Wait-Verification @((Start-VerificationProcess 'charm' $false))}

if($Suite -in @('Reroll','All')) {Write-Output 'Starting native reward reroll verification';Wait-Verification @((Start-VerificationProcess 'reroll' $false))}

if($Suite -in @('Burrow','All')) {Write-Output 'Starting native burrow verification';Wait-Verification @((Start-VerificationProcess 'burrow' $false))}

if($Suite -in @('RewardPerformance','All')) {Write-Output 'Starting native reward performance verification';Wait-Verification @((Start-VerificationProcess 'reward_performance' $false))}

if($Suite -in @('TwinSlime','All')) {Write-Output 'Starting native twin slime verification';Wait-Verification @((Start-VerificationProcess 'twin_slime' $false))}

if($Suite -in @('Formation','All')) {Write-Output 'Starting native friendly formation verification';Wait-Verification @((Start-VerificationProcess 'formation' $false))}

# Recovery suites each need their own fresh/archive/restart pair and report root.
if($Suite -eq 'All') {
 foreach($recoverySuite in @('Recovery','RewardRecovery','NewEffectsNetwork','FourPlayers','ResetNetwork','ServiceRecovery','EventRecovery','MenuResume')) {
  & $PSCommandPath -Suite $recoverySuite -GameRoot $GameRoot
 }
 & $PSCommandPath -Suite EventRecovery -EventGold -GameRoot $GameRoot
 & $PSCommandPath -Suite EventRecovery -EventInProgress -GameRoot $GameRoot
 & $PSCommandPath -Suite EventRecovery -EventPage Initial -GameRoot $GameRoot
 & $PSCommandPath -Suite EventRecovery -EventPage Rest -GameRoot $GameRoot
 & $PSCommandPath -Suite ResetNetwork -ResetEmptyMap -GameRoot $GameRoot
}

if($Suite -in @('Combinations','All')) {Write-Output 'Starting native acquired-effect combinations';Wait-Verification @((Start-VerificationProcess 'combinations' $false))}

if($Suite -in @('RewardNetworkPerformance','RewardChoicePerformance','RewardRefreshPerformance','All')) {
 if($Suite -eq 'All') {
  & $PSCommandPath -Suite RewardNetworkPerformance -RewardBaseline -GameRoot $GameRoot
  & $PSCommandPath -Suite RewardNetworkPerformance -GameRoot $GameRoot
  & (Join-Path $PSScriptRoot 'Compare-RewardPerformance.ps1')
  & $PSCommandPath -Suite RewardChoicePerformance -RewardBaseline -GameRoot $GameRoot
  & $PSCommandPath -Suite RewardChoicePerformance -GameRoot $GameRoot
  & (Join-Path $PSScriptRoot 'Compare-RewardChoices.ps1')
  & $PSCommandPath -Suite RewardRefreshPerformance -RewardBaseline -GameRoot $GameRoot
  & $PSCommandPath -Suite RewardRefreshPerformance -GameRoot $GameRoot
  & (Join-Path $PSScriptRoot 'Compare-RewardRefresh.ps1')
 }
 else {Write-Output 'Starting four-player late reward performance verification';Wait-Verification @((Start-VerificationProcess 'host' $false),(Start-VerificationProcess 'client' $false),(Start-VerificationProcess 'peer3' $false),(Start-VerificationProcess 'peer4' $false))}
}
