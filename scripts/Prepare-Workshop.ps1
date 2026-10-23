param([string]$GameRoot='D:\SteamLibrary\steamapps\common\Slay the Spire 2')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build.ps1') -GameRoot $GameRoot
$workspace=Join-Path $repoRoot 'artifacts\workshop'
$content=Join-Path $workspace 'content'
$identity=Join-Path $repoRoot 'packaging\workshop-item-id.txt'
$workspaceIdentity=Join-Path $workspace 'mod_id.txt'
if(Test-Path -LiteralPath $identity) {
  $expectedId=(Get-Content -LiteralPath $identity -Raw).Trim()
  if($expectedId -notmatch '^\d+$') {throw 'Invalid published workshop identity.'}
  New-Item -ItemType Directory -Path $workspace -Force | Out-Null
  if((Test-Path -LiteralPath $workspaceIdentity) -and (Get-Content -LiteralPath $workspaceIdentity -Raw).Trim() -ne $expectedId) {throw 'Workshop identity mismatch; refusing to prepare a different item.'}
  [IO.File]::WriteAllText($workspaceIdentity,$expectedId)
}

New-Item -ItemType Directory -Path $content -Force | Out-Null
foreach($name in @('FendaoZhengfeng.dll','FendaoZhengfeng.json')) {
  Copy-Item -LiteralPath (Join-Path $repoRoot "artifacts\FendaoZhengfeng\$name") -Destination $content -Force
}
$metadata=Join-Path $workspace 'workshop.json'
if(!(Test-Path -LiteralPath $metadata)) {Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\workshop.json') -Destination $metadata}
# The user requested friends-only testing; preserved workspaces may still carry
# an older private/public setting. Preserve identity and text, enforce this scope.
$workshopInfo=Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
$workshopInfo | Add-Member -NotePropertyName visibility -NotePropertyValue 'friends_only' -Force
[IO.File]::WriteAllText($metadata,($workshopInfo | ConvertTo-Json -Depth 16))
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\DISTRIBUTION.md') -Destination (Join-Path $workspace 'README.md') -Force
Write-Output "Workshop workspace prepared: $workspace"
Write-Output 'Friends-only workspace; this does not upload. Add an original image.png smaller than 1MB, review metadata and retain mod_id.txt after first publication.'
