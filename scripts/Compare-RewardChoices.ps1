param([string]$ReportRoot=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\verification'))
$ErrorActionPreference='Stop'
function P([double[]]$V,[double]$Q) {$a=@($V | Sort-Object);return [double]$a[[Math]::Max(0,[int][Math]::Ceiling($a.Count*$Q)-1)]}
$all=@{};$kinds=@('card','relic','sacrifice','boss_choice')
foreach($mode in @('RewardChoicePerformance-baseline','RewardChoicePerformance')) {
 $all[$mode]=@()
 foreach($id in 1..4) {
  $role=@('host','client','peer3','peer4')[$id-1]
  $log=Get-Content -LiteralPath (Join-Path $ReportRoot "network\$role-$mode.log") -Raw
  if(!$log.Contains('[FDZF REWARD NETWORK COMPLETE]')) {throw "Incomplete native suite: $role $mode"}
  $report=Get-Content -LiteralPath (Join-Path $ReportRoot "$mode\late-choices-$id.json") -Raw | ConvertFrom-Json
  if($report.Player -ne "$id" -or $report.StartDeck -ne 160 -or $report.EndDeck -ne 162 -or $report.Samples.Count -ne 8 -or @($report.Samples.Round | Sort-Object -Unique).Count -ne 8) {throw "Invalid reward choice fixture: $id $mode"}
  foreach($kind in $kinds) {if(@($report.Samples | Where-Object Kind -eq $kind).Count -ne 2) {throw 'Missing reward kind.'}}
  if(@($report.Samples | Where-Object {$null -eq $_.ReadyMs}).Count) {throw 'Card input readiness was not measured.'}
  if($id -ne 1) {$all[$mode]+=$report.Samples}
 }
}
$rows=@();$failures=@()
foreach($kind in $kinds) {
 $before=@($all['RewardChoicePerformance-baseline'] | Where-Object Kind -eq $kind)
 $after=@($all['RewardChoicePerformance'] | Where-Object Kind -eq $kind)
 $bm=P @($before.CommitMs) .5;$am=P @($after.CommitMs) .5;$bp=P @($before.CommitMs) .95;$ap=P @($after.CommitMs) .95;$op=P @($after.OpenMs) .95
 $candidateBefore=P @($before.IntermediateMs) .95;$candidateAfter=P @($after.IntermediateMs) .95
 $rows+=@{BaselineCandidateP95=$candidateBefore;Kind=$kind;ClientSamples=$after.Count;BaselineMedian=$bm;OptimizedMedian=$am;BaselineP95=$bp;OptimizedP95=$ap;OpenP95=$op;ReadyP95=(P @($after.ReadyMs) .95);CandidateOpenP95=(P @($after.IntermediateMs) .95)}
 if($ap -gt 250 -or $op -gt 100 -or ($kind -eq 'sacrifice' -and $candidateAfter -gt 150)) {$failures+=$kind}
}
$json=$rows | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $ReportRoot 'reward-choice-comparison.json'),$json)
Write-Output $json
if($failures.Count) {throw "Choice performance gate failed: $($failures -join ', '). Require commit P95 <=250ms, visible open P95 <=100ms and candidate opening P95 <=150ms; relative baseline changes are diagnostic only; native card input protection is measured separately."}
Write-Output 'PASS late native choice absolute latency gate; baseline is diagnostic only'
