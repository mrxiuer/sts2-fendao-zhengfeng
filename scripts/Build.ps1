param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Slay the Spire 2',[switch]$Deploy)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$dotnetPath=Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if(!(Test-Path -LiteralPath $dotnetPath)) {$dotnetPath=(Get-Command dotnet -ErrorAction Stop).Source}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_NOLOGO='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_HOME=Join-Path $repoRoot '.tools\cli-home'
$env:NUGET_PACKAGES=Join-Path $repoRoot '.tools\packages'
& $dotnetPath build (Join-Path $repoRoot 'src\FendaoZhengfeng.Mod\FendaoZhengfeng.Mod.csproj') -c Release "-p:GameRoot=$GameRoot"
if($LASTEXITCODE -ne 0) {throw 'MOD compilation failed.'}
& $dotnetPath run --project (Join-Path $repoRoot 'tests\FendaoZhengfeng.Tests') -c Release
if($LASTEXITCODE -ne 0) {throw 'Rules verification failed.'}
$packageRoot=Join-Path $repoRoot 'artifacts\FendaoZhengfeng'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
$buildRoot=Join-Path $repoRoot 'src\FendaoZhengfeng.Mod\bin\Release\net9.0'
foreach($file in @('FendaoZhengfeng.dll','FendaoZhengfeng.json')) {
  Copy-Item -LiteralPath (Join-Path $buildRoot $file) -Destination $packageRoot -Force
}
if($Deploy) {
  $modsRoot=[IO.Path]::GetFullPath((Join-Path $GameRoot 'mods'))
  $deploymentPath=[IO.Path]::GetFullPath((Join-Path $modsRoot 'FendaoZhengfeng'))
  if((Split-Path -Parent $deploymentPath) -ne $modsRoot) {throw 'Deployment boundary check failed.'}
  New-Item -ItemType Directory -Path $deploymentPath -Force | Out-Null
  Get-ChildItem -LiteralPath $packageRoot -File | Copy-Item -Destination $deploymentPath -Force
  Write-Output "Deployed MOD files only: $deploymentPath"
}
Write-Output "Package ready: $packageRoot"
