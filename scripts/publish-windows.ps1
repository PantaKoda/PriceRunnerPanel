# Builds the portable Windows release: a self-contained win-x64 publish zipped as
#   artifacts/release/PriceRunnerPanel-<version>-win-x64.zip  (+ .sha256)
# The .NET runtime is included, so the zip runs on Windows 10 1809+ x64 without installing .NET.
# The folder carries release.json: only such a copy may replace itself with a newer release (in-app updates).
#
#   pwsh scripts/publish-windows.ps1                    # locked restore, build, publish, zip
#   pwsh scripts/publish-windows.ps1 -Version 0.0.1     # a local test build that reports an older version
#
# Signing is not done here: signing keys never belong in this repository.
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Output = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Invoke-Checked([string]$what, [scriptblock]$command) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

$project = 'src/PriceRunnerPanel/PriceRunnerPanel.csproj'
$runtime = 'win-x64'
$commit = (git rev-parse HEAD).Trim()
if (git status --porcelain) {
    Write-Warning 'The working tree has uncommitted or untracked files: this build is not reproducible from the commit and must not be released.'
}
# Zip timestamps come from the commit so they do not depend on when or where the build ran.
$stamp = [DateTimeOffset]::FromUnixTimeSeconds([long](git log -1 --format=%ct HEAD)).UtcDateTime

if (-not $Version) { $Version = (dotnet msbuild $project -getProperty:Version).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected version '$Version'." }
$name = "PriceRunnerPanel-$Version-$runtime"
$publishDir = Join-Path $root "artifacts/publish/$runtime"
$outDir = Join-Path $root $Output
$zip = Join-Path $outDir "$name.zip"

Invoke-Checked 'Restore (locked)' { dotnet restore PriceRunnerPanel.slnx --locked-mode }

# Start from empty output folders so files from an earlier publish cannot leak into the zip.
foreach ($dir in $publishDir, (Join-Path $root "src/PriceRunnerPanel/obj/Release")) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
Invoke-Checked 'Publish' {
    dotnet publish $project -c Release -r $runtime --self-contained -p:RestoreLockedMode=true -p:Version=$Version `
        -p:ContinuousIntegrationBuild=true -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en -o $publishDir
}
if (-not (Test-Path (Join-Path $publishDir 'PriceRunnerPanel.exe'))) { throw 'The publish did not produce PriceRunnerPanel.exe.' }

# "files" lists everything the release ships (ordinal order). The updater only replaces a folder that holds nothing
# else, so an update can never move or delete files a user happened to keep next to the app.
$shipped = [string[]](Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/') }) + 'release.json'
[Array]::Sort($shipped, [StringComparer]::Ordinal)
$manifest = [ordered]@{ version = $Version; commit = $commit; runtime = $runtime; files = $shipped }
Set-Content -Path (Join-Path $publishDir 'release.json') -Value ($manifest | ConvertTo-Json -Compress) -Encoding utf8 -NoNewline

Write-Host '==> Zip' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $outDir | Out-Null
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression
$files = @(Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { [pscustomobject]@{ File = $_; Entry = 'PriceRunnerPanel/' + [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/') } })
# Ordinal order, so the entry order cannot depend on the culture.
$entries = [string[]]($files | ForEach-Object Entry)
$items = [object[]]$files
[Array]::Sort($entries, $items, [StringComparer]::Ordinal)
$stream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
try {
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $items) {
            $entry = $archive.CreateEntry($item.Entry, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $stamp
            $source = $item.File.OpenRead()
            $target = $entry.Open()
            try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Host ''
Write-Host "Version : $Version ($($commit.Substring(0, 7)))"
Write-Host "Zip     : $zip ($size MB, $($items.Count) files)"
Write-Host "SHA-256 : $hash"
Write-Host "Install : extract the zip to %LOCALAPPDATA%\Programs, then start PriceRunnerPanel\PriceRunnerPanel.exe"
