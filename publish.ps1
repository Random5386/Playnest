# Builds Playnest for Windows.
#
#   .\publish.ps1            -> native (NativeAOT) build: one small exe, fastest start, lowest RAM.
#                               Needs Visual Studio's "Desktop development with C++" workload (or the VS Build Tools).
#   .\publish.ps1 -Portable  -> no C++ tools needed: trimmed self-contained single exe (a bit larger, still no install).
#
# Output: .\publish\Playnest\Playnest.exe

param([switch]$Portable)

$ErrorActionPreference = "Stop"
$env:AVALONIA_TELEMETRY_OPTOUT = "1"   # no anonymous build telemetry
$project = Join-Path $PSScriptRoot "src\Playnest\Playnest.csproj"
$out = Join-Path $PSScriptRoot "publish\Playnest"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
    exit 1
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

if ($Portable) {
    dotnet publish $project -c Release -r win-x64 -o $out `
        -p:PublishAot=false -p:SelfContained=true -p:PublishSingleFile=true -p:PublishTrimmed=true `
        -p:PublishReadyToRun=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none
} else {
    dotnet publish $project -c Release -r win-x64 -o $out -p:PublishAot=true -p:DebugType=none
}

if ($LASTEXITCODE -ne 0) {
    if (-not $Portable) {
        Write-Host "`nNative build failed. If the error mentions link.exe or the C++ tools, either install" -ForegroundColor Yellow
        Write-Host "Visual Studio's 'Desktop development with C++' workload, or run:  .\publish.ps1 -Portable" -ForegroundColor Yellow
    }
    exit $LASTEXITCODE
}

Get-ChildItem $out -Filter *.pdb | Remove-Item -ErrorAction SilentlyContinue
$exe = Join-Path $out "Playnest.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "`nDone: $exe ($size MB)" -ForegroundColor Green
