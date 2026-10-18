param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Slay the Spire 2')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build.ps1') -GameRoot $GameRoot
$workspace=Join-Path $repoRoot 'artifacts\workshop'
$content=Join-Path $workspace 'content'
New-Item -ItemType Directory -Path $content -Force | Out-Null
foreach($name in @('FendaoZhengfeng.dll','FendaoZhengfeng.json')) {
  Copy-Item -LiteralPath (Join-Path $repoRoot "artifacts\FendaoZhengfeng\$name") -Destination $content -Force
}
$metadata=Join-Path $workspace 'workshop.json'
if(!(Test-Path -LiteralPath $metadata)) {Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\workshop.json') -Destination $metadata}
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\DISTRIBUTION.md') -Destination (Join-Path $workspace 'README.md') -Force
Write-Output "Workshop workspace prepared: $workspace"
Write-Output 'This does not upload. Add an original image.png smaller than 1MB, review metadata and retain mod_id.txt after first publication.'
