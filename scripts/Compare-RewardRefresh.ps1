param([string]$ReportRoot=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\verification'),[switch]$OptimizedOnly)
$ErrorActionPreference='Stop'
function P([double[]]$V,[double]$Q) {$a=@($V | Sort-Object);return [double]$a[[Math]::Max(0,[int][Math]::Ceiling($a.Count*$Q)-1)]}
$all=@{};$kinds=@('potion','card_refresh','potion_refresh','relic_refresh','offer_refresh')
$modes=if($OptimizedOnly) {@('RewardRefreshPerformance')} else {@('RewardRefreshPerformance-baseline','RewardRefreshPerformance')}
foreach($mode in $modes) {
 $all[$mode]=@()
 foreach($id in 1..4) {
  $role=@('host','client','peer3','peer4')[$id-1]
  $log=Get-Content -LiteralPath (Join-Path $ReportRoot "network\$role-$mode.log") -Raw
  if(!$log.Contains('[FDZF REWARD NETWORK COMPLETE]')) {throw "Incomplete refresh process: $role $mode"}
  $report=Get-Content -LiteralPath (Join-Path $ReportRoot "$mode\late-refresh-$id.json") -Raw | ConvertFrom-Json
  if($report.Player -ne "$id" -or $report.Deck -ne 160 -or $report.PotionGains -ne 2 -or $report.Samples.Count -ne 10 -or @($report.Samples.Round | Sort-Object -Unique).Count -ne 10) {throw "Invalid refresh fixture: $id $mode"}
  foreach($kind in $kinds) {if(@($report.Samples | Where-Object Kind -eq $kind).Count -ne 2) {throw 'Missing refresh kind.'}}
  if($id -ne 1) {$all[$mode]+=$report.Samples}
 }
}
$rows=@();$failures=@()
foreach($kind in $kinds) {
 $before=if($OptimizedOnly) {@()} else {@($all['RewardRefreshPerformance-baseline'] | Where-Object Kind -eq $kind)}
 $after=@($all['RewardRefreshPerformance'] | Where-Object Kind -eq $kind)
 $bm=if($before.Count) {P @($before.Milliseconds) .5} else {$null};$bp=if($before.Count) {P @($before.Milliseconds) .95} else {$null}
 $am=P @($after.Milliseconds) .5;$ap=P @($after.Milliseconds) .95
 $rows+=@{Kind=$kind;ClientSamples=$after.Count;BaselineMedian=$bm;OptimizedMedian=$am;BaselineP95=$bp;OptimizedP95=$ap}
 if($ap -gt 250) {$failures+=$kind}
}
$json=$rows | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $ReportRoot 'reward-refresh-comparison.json'),$json)
Write-Output $json
if($failures.Count) {throw "Refresh performance gate failed: $($failures -join ', '). Require P95 <=250ms; relative baseline changes are diagnostic only."}
Write-Output 'PASS late native refresh and potion absolute performance gate'
