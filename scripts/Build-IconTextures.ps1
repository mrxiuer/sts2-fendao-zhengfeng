param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Slay the Spire 2')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$source=Join-Path $repoRoot 'src\FendaoZhengfeng.Mod\Assets\icons'
$destination=Join-Path $repoRoot 'src\FendaoZhengfeng.Mod\Assets\icon-textures'
$manifestPath=Join-Path $destination 'manifest.json'
$sourceFiles=@(Get-ChildItem -LiteralPath $source -File -Filter '*.png' | Sort-Object Name)
$hashes=[ordered]@{}
foreach($file in $sourceFiles) {$hashes[$file.Name]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
$previous=if(Test-Path -LiteralPath $manifestPath) {Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable} else {@{}}
$stale=$previous.Count -ne $hashes.Count
foreach($name in $hashes.Keys) {
 $target=Join-Path $destination $name
 if(!(Test-Path -LiteralPath $target)) {$stale=$true;continue}
 $expected=$hashes[$name]+':'+(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
 if($previous[$name] -ne $expected) {$stale=$true}
}
if(!$stale) {Write-Output "Icon textures current: $($hashes.Count)";return}
if(@(Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue).Count -gt 0) {throw 'Close game processes before regenerating icon textures.'}
$launch=[Diagnostics.ProcessStartInfo]::new()
$launch.FileName=Join-Path $GameRoot 'SlayTheSpire2.exe'
$launch.WorkingDirectory=Join-Path $PSScriptRoot 'icon-builder'
$launch.UseShellExecute=$false
$launch.CreateNoWindow=$true
$launch.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$launch.RedirectStandardOutput=$true
$launch.RedirectStandardError=$true
foreach($argument in @('--headless','--path',$launch.WorkingDirectory,'--script','build.gd','--',$source,$destination)) {$launch.ArgumentList.Add($argument)}
$process=[Diagnostics.Process]::Start($launch)
$outputTask=$process.StandardOutput.ReadToEndAsync()
$errorTask=$process.StandardError.ReadToEndAsync()
if(!$process.WaitForExit(60000)) {$process.Kill();throw 'Icon texture build timed out.'}
$output=$outputTask.GetAwaiter().GetResult();$errors=$errorTask.GetAwaiter().GetResult()
if($process.ExitCode -ne 0 -or !$output.Contains("FDZF_ICON_BUILD_PASS count=$($hashes.Count)")) {throw "Icon texture build failed: $output $errors"}
# PNG headers also verify that no source-size file was copied into the bundle.
foreach($name in $hashes.Keys) {
 $data=[IO.File]::ReadAllBytes((Join-Path $destination $name))
 if($data.Length -lt 24 -or [Convert]::ToHexString($data[16..23]) -ne '0000008000000080') {throw "Invalid icon texture size: $name"}
}
foreach($name in @($hashes.Keys)) {$hashes[$name]+=':'+(Get-FileHash -LiteralPath (Join-Path $destination $name) -Algorithm SHA256).Hash}
[IO.File]::WriteAllText($manifestPath,($hashes | ConvertTo-Json))
Write-Output "PASS $($hashes.Count) display textures match original runtime pixels"
