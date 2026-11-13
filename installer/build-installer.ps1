# Build SoundPipe installer: dotnet publish (self-contained single file) + Inno Setup compile
# Usage: powershell -File installer\build-installer.ps1   (run from repo root)
# NOTE: ASCII only - Windows PowerShell 5 reads .ps1 as ANSI/GBK and CJK breaks parsing.
$ErrorActionPreference = 'Stop'

$root = (Get-Location).Path
$installerDir = Join-Path $root 'installer'
$publishDir = Join-Path $installerDir 'publish'

# ---- locate dotnet with SDK (PATH dotnet may be runtime-only on this machine) ----
$dotnet = $null
if ($env:DOTNET_EXE -and (Test-Path $env:DOTNET_EXE)) { $dotnet = $env:DOTNET_EXE }
else {
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $userDotnet) { $dotnet = $userDotnet }
    elseif (Get-Command dotnet -ErrorAction SilentlyContinue) { $dotnet = 'dotnet' }
}
if (-not $dotnet) { throw 'dotnet with .NET 8 SDK not found' }
Write-Host "dotnet: $dotnet"

# ---- publish self-contained single file ----
$proj = Join-Path $root 'SoundPipe.App\SoundPipe.App.csproj'
& $dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir --nologo -v m
if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $publishDir 'SoundPipe.exe'
if (-not (Test-Path $exe)) { throw "published exe missing: $exe" }
Write-Host ("published: {0} ({1:N0} bytes)" -f $exe, (Get-Item $exe).Length)

# ---- locate Inno Setup compiler ----
$iscc = $null
foreach ($cand in @(
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
    if (Test-Path $cand) { $iscc = $cand; break }
}
if (-not $iscc) {
    Write-Error 'Inno Setup not found. Install it from https://github.com/jrsoftware/issrc/releases'
    exit 1
}
Write-Host "iscc: $iscc"

# ---- compile installer ----
Push-Location $installerDir
try {
    & $iscc installer.iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }

$out = Get-ChildItem (Join-Path $installerDir 'dist') -Filter 'SoundPipe-Setup-*.exe' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $out) { throw "installer output missing in installer\dist" }
Write-Host ("OK: {0} ({1:N0} bytes)" -f $out.FullName, $out.Length)
