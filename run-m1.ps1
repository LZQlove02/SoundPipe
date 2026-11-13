# SoundPipe M1 launcher
# Usage: .\run-m1.ps1              -> forward to default target (Voicemeeter VAIO)
#        .\run-m1.ps1 list         -> enumerate playback devices
#        .\run-m1.ps1 run --index 0 --gain 1.5
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs)

$exe = Join-Path $PSScriptRoot "SoundPipe.Cli\bin\Debug\net8.0-windows\SoundPipe.Cli.exe"
if (-not (Test-Path $exe)) {
    Write-Host "CLI not found. Build with SDK first." -ForegroundColor Yellow
    exit 1
}
& $exe @AppArgs
