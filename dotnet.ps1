# SoundPipe dev entry: run .NET SDK commands when PATH dotnet is runtime-only.
# Usage: .\dotnet.ps1 build SoundPipe.sln
#        .\dotnet.ps1 test SoundPipe.Tests
#        .\dotnet.ps1 run --project SoundPipe.Cli
# Resolution order: $env:DOTNET_EXE -> %USERPROFILE%\.dotnet\dotnet.exe -> PATH dotnet
# No param block: keeps -v/-n etc. from being bound as PowerShell common parameters.

$dotnet = $null
if ($env:DOTNET_EXE -and (Test-Path $env:DOTNET_EXE)) {
    $dotnet = $env:DOTNET_EXE
}
else {
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $userDotnet) { $dotnet = $userDotnet }
    elseif (Get-Command dotnet -ErrorAction SilentlyContinue) { $dotnet = 'dotnet' }
}
if (-not $dotnet) { throw 'dotnet with .NET 8 SDK not found. Set DOTNET_EXE or install the SDK.' }

& $dotnet @args
exit $LASTEXITCODE
