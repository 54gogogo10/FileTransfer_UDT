# Smoke: per-protocol save directories (one dir box per server tab, frozen only
# while THAT tab serves). One-shot driver - seeds a fresh config (real config.ini
# is backed up and restored), launches the freshly built exe, drives UIA:
#   A. migration: legacy SaveDir shows on TCP tab, UDP tab starts at the same value
#   B. per-tab freeze: starting TCP freezes only TCP's dir box; UDP's stays editable
#   C. per-tab receive: UDP server (UDP tab) saves into UDP's dir via CLI --udp send
#   D. persistence: on close, config.ini carries SaveDirTCP/SaveDirUDP/legacy mirror
# Re-test: close all TrFileTransfer instances, rebuild, then
#   powershell -ExecutionPolicy Bypass -File step_savedir_smoke.ps1
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\uia_common.ps1"

$exe  = 'D:\zcode\TrFileTransfer\TrFileTransfer\TrFileTransfer.exe'
if (-not (Test-Path $exe)) { throw "exe not found: $exe (run build.bat first)" }
$running = Get-Process -Name 'TrFileTransfer' -ErrorAction SilentlyContinue
if ($running -ne $null) { throw "an instance is already running (pid $($running.Id)) - close it first" }

$base  = Join-Path $env:TEMP 'gui_savedir_test'
$recvA = Join-Path $base 'recvA'
$recvB = Join-Path $base 'recvB'
New-Item -ItemType Directory -Force -Path $recvA, $recvB, (Join-Path $base 'send') | Out-Null
$sendFile = Join-Path $base 'send\hello.txt'
'Savedir smoke payload' | Set-Content -Path $sendFile -Encoding ASCII

# --- backup + seed the real config location (ASCII-only keys: English UI) ---
$cfgDir = Join-Path $env:APPDATA 'TrFileTransfer'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$cfg = Join-Path $cfgDir 'config.ini'
$bak = Join-Path $cfgDir 'config.ini.smoke_bak'
$hadCfg = Test-Path $cfg
if ($hadCfg) { Copy-Item $cfg $bak -Force }
$lines = @(
    "Language=English",
    "ClientIP=127.0.0.1",
    "ClientPort=47881",
    "ServerPortTCP=47882",
    "ServerPortUDT=47883",
    "ServerPortUDP=47881",
    "LastPath=$sendFile",
    "SaveDir=$recvA"          # legacy single key - no SaveDirTCP/UDT/UDP yet
)
[System.IO.File]::WriteAllLines($cfg, $lines)   # UTF-8 without BOM, like the app

try {
    # --- launch ---
    $proc = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 4
    $win = Get-AppWindow $proc.Id
    Write-Output ("window: " + $win.Current.Name)

    function Select-TabPrefix($root, [string]$prefix) {
        # headers gain a running marker suffix, so match by prefix
        $tabs = Find-All $root ([System.Windows.Automation.ControlType]::TabItem)
        foreach ($t in $tabs) {
            if ($t.Current.Name.StartsWith($prefix)) {
                ($t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                return
            }
        }
        throw "tab '$prefix' not found"
    }

    # --- A. migration: TCP tab (default) shows the legacy dir ---
    $tcpEdit = Get-EditTextByValue $win $recvA
    if ($tcpEdit -eq $null) { throw "FAIL A: migrated save dir not shown on TCP tab" }
    Write-Output "PASS A1: TCP tab shows migrated legacy dir"

    Select-TabPrefix $win 'UDP'
    Start-Sleep -Milliseconds 500
    $udpEdit = Get-EditTextByValue $win $recvA
    if ($udpEdit -eq $null) { throw "FAIL A: UDP tab did not inherit legacy dir" }
    Write-Output "PASS A2: UDP tab starts at the same legacy dir"
    ($udpEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($recvB)
    Start-Sleep -Milliseconds 300
    if ((Get-EditTextByValue $win $recvB) -eq $null) { throw "FAIL A: UDP dir rewrite failed" }
    Write-Output "PASS A3: UDP dir diverged to recvB"

    # --- B. start TCP; only TCP's dir freezes ---
    Select-TabPrefix $win 'TCP'
    Start-Sleep -Milliseconds 400
    Invoke-Button $win 'Start'
    Start-Sleep -Seconds 2
    $tcpEdit2 = Get-EditTextByValue $win $recvA
    if ($tcpEdit2 -eq $null) { throw "FAIL B: TCP dir box vanished" }
    if ($tcpEdit2.Current.IsEnabled) { throw "FAIL B: TCP dir still editable while TCP runs" }
    Write-Output "PASS B1: TCP dir frozen while TCP runs"

    Select-TabPrefix $win 'UDP'
    Start-Sleep -Milliseconds 400
    $udpEdit2 = Get-EditTextByValue $win $recvB
    if ($udpEdit2 -eq $null) { throw "FAIL B: UDP dir box vanished" }
    if (-not $udpEdit2.Current.IsEnabled) { throw "FAIL B: UDP dir frozen while TCP runs (must stay editable)" }
    Write-Output "PASS B2: UDP dir stays editable while TCP runs (core requirement)"

    # --- C. UDP tab server receives into ITS dir ---
    Invoke-Button $win 'Start'
    Start-Sleep -Seconds 2
    $log = Get-LogText $win
    Write-Output ("C: udp start logged: " + ($log -match 'UDP'))

    $null = & $exe send --udp --ip 127.0.0.1 --port 47881 --file $sendFile --passes 3 2>&1
    Write-Output ("C: cli send exit code: " + $LASTEXITCODE)
    $landed = Join-Path $recvB 'hello.txt'
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $landed)) { Start-Sleep -Milliseconds 300 }
    if (-not (Test-Path $landed)) { throw "FAIL C: file did not land in UDP dir" }
    Write-Output "PASS C1: file landed in UDP tab's dir (recvB)"
    $recvAFiles = @(Get-ChildItem -Recurse -File $recvA)
    if ($recvAFiles.Count -ne 0) { throw "FAIL C: TCP dir received something" }
    Write-Output "PASS C2: TCP dir untouched"

    # --- D. stop, close (cancel-close saves config), verify persistence ---
    Invoke-Button $win 'Stop'
    Start-Sleep -Milliseconds 800
    $udpEdit3 = Get-EditTextByValue $win $recvB
    if ($udpEdit3 -ne $null -and -not $udpEdit3.Current.IsEnabled) { throw "FAIL D: UDP dir not re-enabled after stop" }
    Write-Output "PASS D1: UDP dir re-enabled after stop"

    ($win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
    Start-Sleep -Seconds 2
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    $saved = Get-Content $cfg
    $tcpKey  = ($saved | Select-String '^SaveDirTCP=').Line
    $udpKey  = ($saved | Select-String '^SaveDirUDP=').Line
    $mirrKey = ($saved | Select-String '^SaveDir=').Line
    Write-Output $tcpKey
    Write-Output $udpKey
    Write-Output ("legacy mirror: " + $mirrKey)
    if ($tcpKey -ne "SaveDirTCP=$recvA") { throw "FAIL D: SaveDirTCP not persisted" }
    if ($udpKey -ne "SaveDirUDP=$recvB") { throw "FAIL D: SaveDirUDP not persisted" }
    Write-Output "PASS D2: per-tab dirs persisted; legacy mirror kept for downgrade"

    Write-Output "=== ALL SMOKE CHECKS PASSED ==="
}
finally {
    # --- restore the real config + temp dirs ---
    if ($hadCfg) { Move-Item $bak $cfg -Force }
    elseif (Test-Path $cfg) { Remove-Item $cfg -Force }
    Get-Process -Name 'TrFileTransfer' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $base -ErrorAction SilentlyContinue
    Write-Output "cleanup done (real config restored)"
}
