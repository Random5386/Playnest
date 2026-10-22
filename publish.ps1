# Builds Playnest for Windows.
#
#   .\publish.ps1          -> ONE self-contained Playnest.exe (trimmed, compressed, ReadyToRun). This is what releases
#                             ship: copy the exe anywhere and run it, nothing else needed.
#   .\publish.ps1 -Native  -> NativeAOT build (fastest start, least RAM). Needs Visual Studio's "Desktop development
#                             with C++" workload. NOTE: it is a FOLDER, not one file: Playnest.exe needs the native
#                             DLLs next to it (libSkiaSharp.dll, libHarfBuzzSharp.dll, av_libglesv2.dll), so never
#                             ship the exe on its own.
#
# Output: .\publish\Playnest\

param(
    [switch]$Native,
    [switch]$Portable   # kept for older instructions; the single-file build is now the default
)

$ErrorActionPreference = "Stop"
$env:AVALONIA_TELEMETRY_OPTOUT = "1"   # no anonymous build telemetry
$project = Join-Path $PSScriptRoot "src\Playnest\Playnest.csproj"
$out = Join-Path $PSScriptRoot "publish\Playnest"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
    exit 1
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

if ($Native) {
    dotnet publish $project -c Release -r win-x64 -o $out -p:PublishAot=true -p:DebugType=none
} else {
    dotnet publish $project -c Release -r win-x64 -o $out `
        -p:PublishAot=false -p:SelfContained=true -p:PublishSingleFile=true -p:PublishTrimmed=true `
        -p:PublishReadyToRun=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none
}

if ($LASTEXITCODE -ne 0) {
    if ($Native) {
        Write-Host "`nNative build failed. If the error mentions link.exe or the C++ tools, either install" -ForegroundColor Yellow
        Write-Host "Visual Studio's 'Desktop development with C++' workload, or run:  .\publish.ps1" -ForegroundColor Yellow
    }
    exit $LASTEXITCODE
}

Get-ChildItem $out -Filter *.pdb | Remove-Item -ErrorAction SilentlyContinue

# The single-file build must really be one file: anything else next to the exe means it wouldn't run on its own.
if (-not $Native) {
    $extra = Get-ChildItem $out -File | Where-Object { $_.Name -ne "Playnest.exe" }
    if ($extra) {
        Write-Host "`nThe build left other files next to Playnest.exe ($($extra.Name -join ', ')); the exe wouldn't run on its own." -ForegroundColor Red
        exit 1
    }
}
$exe = Join-Path $out "Playnest.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)

# SHA-256 checksum, so people can check the exe they downloaded is exactly the one you published.
# Paste the line into the GitHub release notes and/or attach SHA256SUMS.txt to the release.
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$sums = Join-Path $out "SHA256SUMS.txt"
Set-Content -Path $sums -Value "$hash  Playnest.exe" -Encoding ascii

Write-Host "`nDone: $exe ($size MB)" -ForegroundColor Green
if ($Native) { Write-Host "Native build: keep every file in $out together (the exe needs the DLLs next to it)." -ForegroundColor Yellow }
Write-Host "SHA-256: $hash" -ForegroundColor Green
Write-Host "(saved to $sums - put it in the release notes)"
