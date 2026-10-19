param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory) {$OutputDirectory=Join-Path $repoRoot 'artifacts\releases'}
$packageRoot=Join-Path $repoRoot 'artifacts\FendaoZhengfeng'
$manifest=Get-Content -LiteralPath (Join-Path $packageRoot 'FendaoZhengfeng.json') -Raw | ConvertFrom-Json
$version=$manifest.version
if($version -notmatch '^\d+\.\d+\.\d+$') {throw 'Invalid package version.'}
$dll=Join-Path $packageRoot 'FendaoZhengfeng.dll'
if([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion.Split('+')[0] -ne $version) {throw 'DLL and manifest versions differ; build first.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$zipPath=Join-Path $OutputDirectory "FendaoZhengfeng-$version-preview.zip"
$readmePath=Join-Path $repoRoot 'README.md'
$mockPath=Join-Path $repoRoot 'docs\MOCK_TESTING.md'
Add-Type -AssemblyName System.IO.Compression
$stream=[IO.File]::Create($zipPath)
$archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try {
  foreach($name in @('FendaoZhengfeng.dll','FendaoZhengfeng.json')) {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $packageRoot $name),"FendaoZhengfeng/$name",[IO.Compression.CompressionLevel]::Optimal) | Out-Null
  }
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$readmePath,'README.md') | Out-Null
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$mockPath,'模拟测试说明.md') | Out-Null
  Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -File -Recurse | ForEach-Object {
    $entryName=[IO.Path]::GetRelativePath($repoRoot,$_.FullName).Replace('\','/')
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$_.FullName,$entryName) | Out-Null
  }
} finally {$archive.Dispose();$stream.Dispose()}
$checksum=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'),"$checksum  $([IO.Path]::GetFileName($zipPath))`n",[Text.UTF8Encoding]::new($false))
Write-Output "Package ready: $zipPath"
Write-Output "SHA256: $checksum"
