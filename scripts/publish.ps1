# Builds a single-file Windows executable into artifacts/publish/PriceRunnerPanel.exe.
# Framework-dependent (needs the .NET 10 Desktop Runtime). Run from the repository root:  pwsh scripts/publish.ps1
$ErrorActionPreference = 'Stop'
$out = Join-Path (Get-Location) 'artifacts/publish'
dotnet publish src/PriceRunnerPanel/PriceRunnerPanel.csproj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
"Published to $out\PriceRunnerPanel.exe"
