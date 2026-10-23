param([string]$ReportRoot=(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\verification'))
$ErrorActionPreference='Stop'
function Percentile([double[]]$Values,[double]$P) {
 $ordered=@($Values | Sort-Object)
 if(!$ordered.Count) {throw 'No reward samples.'}
 return [double]$ordered[[Math]::Max(0,[int][Math]::Ceiling($ordered.Count*$P)-1)]
}
$rows=@();$baselineClients=@();$optimizedClients=@()
foreach($id in 1..4) {
 $baseline=Get-Content -LiteralPath (Join-Path $ReportRoot "RewardNetworkPerformance-baseline\late-rewards-$id.json") -Raw | ConvertFrom-Json
 $optimized=Get-Content -LiteralPath (Join-Path $ReportRoot "RewardNetworkPerformance\late-rewards-$id.json") -Raw | ConvertFrom-Json
 foreach($data in @($baseline,$optimized)) {
  if($data.Player -ne "$id" -or $data.Deck -ne 160 -or $data.Samples.Count -ne 12 -or @($data.Samples.Round | Sort-Object -Unique).Count -ne 12) {throw "Incomplete fixture for player $id"}
 }
 if([Math]::Abs($baseline.MatchBytes-$optimized.MatchBytes)/[double]$baseline.MatchBytes -gt 0.02) {throw 'A/B state sizes differ by more than 2 percent.'}
 $b=@($baseline.Samples | ForEach-Object {[double]$_.Milliseconds});$o=@($optimized.Samples | ForEach-Object {[double]$_.Milliseconds})
 if($id -ne 1) {$baselineClients+=$b;$optimizedClients+=$o}
 $rows+=@{Player=$id;BaselineMedian=(Percentile $b .5);OptimizedMedian=(Percentile $o .5);BaselineP95=(Percentile $b .95);OptimizedP95=(Percentile $o .95)}
}
$baseMedian=Percentile $baselineClients .5;$newMedian=Percentile $optimizedClients .5
$baseP95=Percentile $baselineClients .95;$newP95=Percentile $optimizedClients .95
$result=@{Rows=$rows;ClientSamples=$optimizedClients.Count;BaselineMedian=$baseMedian;OptimizedMedian=$newMedian;BaselineP95=$baseP95;OptimizedP95=$newP95;MedianRatio=($newMedian/$baseMedian);P95Ratio=($newP95/$baseP95)}
$json=$result | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $ReportRoot 'reward-network-comparison.json'),$json)
Write-Output $json
if($newP95 -gt 150) {throw 'Late native gold reward performance gate failed: require client P95 <=150ms; relative baseline changes are diagnostic only.'}
Write-Output 'PASS late native gold absolute latency gate; baseline is diagnostic only'
