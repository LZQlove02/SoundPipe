# UI smoke test for SoundPipe WPF app (ASCII only!)
# Flow: launch -> wait window -> click StartBtn -> assert StopBtn enabled -> click StopBtn
#       -> assert StartBtn enabled -> kill process
param(
    [string]$Exe = "",
    [switch]$ExpectAutoRunOn
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if ($Exe -eq "") {
    $Exe = Join-Path (Get-Location).Path 'SoundPipe.App\bin\Debug\net8.0-windows\SoundPipe.exe'
}
$exe = $Exe
if (-not (Test-Path $exe)) { throw "exe not found: $exe" }

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4
if ($p.HasExited) { throw "app exited during startup, code=$($p.ExitCode)" }
Write-Host "launched pid=$($p.Id)"

$auto = [System.Windows.Automation.AutomationElement]
$win = $auto::RootElement.FindFirst(
    [System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $p.Id)))
if ($null -eq $win) { throw "main window not found" }
Write-Host "window found"

function Find-ById($parent, $id) {
    return $parent.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, $id)))
}

function Invoke-Button($btn, $name) {
    if ($null -eq $btn) { throw "button not found: $name" }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Test-Enabled($el, $name) {
    if ($null -eq $el) { throw "element not found: $name" }
    return $el.Current.IsEnabled
}

$start = Find-ById $win 'StartBtn'
$stop = Find-ById $win 'StopBtn'
$autorun = Find-ById $win 'AutoRunCheck'
if (-not (Test-Enabled $start 'StartBtn')) { throw "StartBtn should be enabled initially" }
if (Test-Enabled $stop 'StopBtn') { throw "StopBtn should be disabled initially" }

if ($ExpectAutoRunOn) {
    $toggle = $autorun.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        throw "AutoRunCheck should be ON (registry key was set by installer)"
    }
    Write-Host "autorun toggle: ON (synced with registry)"
}

# status text before start
$status = Find-ById $win 'StatusText'
Write-Host "status before: $($status.Current.Name)"

Invoke-Button $start 'StartBtn'
Start-Sleep -Seconds 3
if ($p.HasExited) { throw "app crashed after start" }
if (-not (Test-Enabled $stop 'StopBtn')) { throw "StopBtn should be enabled while running" }
if (Test-Enabled $start 'StartBtn') { throw "StartBtn should be disabled while running" }
Write-Host "status running: $($status.Current.Name)"

# let it run a bit to exercise mixer thread
Start-Sleep -Seconds 3
if ($p.HasExited) { throw "app crashed while running" }

Invoke-Button $stop 'StopBtn'
Start-Sleep -Seconds 2
if (-not (Test-Enabled $start 'StartBtn')) { throw "StartBtn should be enabled after stop" }
if (Test-Enabled $stop 'StopBtn') { throw "StopBtn should be disabled after stop" }
Write-Host "status after: $($status.Current.Name)"

# graceful close via WindowPattern (triggers Window_Closing -> SaveSettings -> Shutdown)
try {
    $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $wp.Close()
} catch {
    Stop-Process -Id $p.Id -Force
    throw "window close failed: $($_.Exception.Message)"
}
Start-Sleep -Seconds 3
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; throw "process did not exit after window close" }

# verify settings persisted
$cfg = Join-Path $env:APPDATA 'SoundPipe\settings.json'
if (Test-Path $cfg) {
    Write-Host "settings.json: OK ($((Get-Item $cfg).Length) bytes)"
    Write-Host "content: $(Get-Content $cfg -Raw)"
} else {
    Write-Host "settings.json: MISSING (SaveSettings may not have run)"
}
Write-Host "SMOKE OK"
