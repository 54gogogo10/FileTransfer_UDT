# Smoke: monitor mode copy-completion gate + one-slot queue deferral.
# Scenario: start monitoring an empty folder, begin a SLOW copy of a big file
# (writer holds the handle open, size grows ~6s), then drop in a small complete
# file. Expected with the new probe: the small file is sent IMMEDIATELY (the
# big one defers one slot instead of blocking the queue head), and the big file
# is only sent after its copy completes - arriving with its FULL size (no
# half-copied file ever goes out). A CLI TCP receiver with --count 2 collects.
# One-shot driver; real config.ini is backed up and restored. Re-test:
#   powershell -ExecutionPolicy Bypass -File step_monitor_smoke.ps1
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\uia_common.ps1"

$exe  = 'D:\zcode\TrFileTransfer\TrFileTransfer\TrFileTransfer.exe'
if (-not (Test-Path $exe)) { throw "exe not found: $exe (run build.bat first)" }
$running = Get-Process -Name 'TrFileTransfer' -ErrorAction SilentlyContinue
if ($running -ne $null) { throw "an instance is already running (pid $($running.Id)) - close it first" }

$base   = Join-Path $env:TEMP 'gui_monitor_test'
$monDir = Join-Path $base 'mon'
$recvDir = Join-Path $base 'recv'
New-Item -ItemType Directory -Force -Path $monDir, $recvDir | Out-Null
$bigPath  = Join-Path $monDir 'aaa_big.bin'
$smallPath = Join-Path $monDir 'bbb_small.txt'
$bigSize = 20MB

# --- backup + seed config (ASCII keys -> English UI; monitor pre-checked) ---
$cfgDir = Join-Path $env:APPDATA 'TrFileTransfer'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$cfg = Join-Path $cfgDir 'config.ini'
$bak = Join-Path $cfgDir 'config.ini.smoke_bak'
$hadCfg = Test-Path $cfg
if ($hadCfg) { Copy-Item $cfg $bak -Force }
$lines = @(
    "Language=English",
    "ClientIP=127.0.0.1",
    "ClientPort=47901",
    "ServerPortTCP=47902",
    "ServerPortUDT=47903",
    "ServerPortUDP=47904",
    "MonitorMode=1",
    "LastPath=$monDir"
)
[System.IO.File]::WriteAllLines($cfg, $lines)

$gui = $null; $cli = $null; $job = $null; $win = $null
try {
    # --- CLI TCP receiver, exits after 2 files ---
    $cliOut = Join-Path $base 'cli_out.txt'
    $cli = Start-Process -FilePath $exe -ArgumentList @('recv','--tcp','--port','47901','--out',$recvDir,'--count','2') `
        -WindowStyle Hidden -RedirectStandardOutput $cliOut -PassThru

    # --- GUI: monitor checkbox comes from seeded MonitorMode=1 ---
    $gui = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 4
    $win = Get-AppWindow $gui.Id
    Write-Output ("window: " + $win.Current.Name)
    Invoke-Button $win 'Start Monitor'
    Write-Output "monitor started"
    Start-Sleep -Seconds 2
    Write-Output "--- GUI log right after start ---"
    Write-Output (Get-LogText $win)
    Write-Output "--- end log ---"

    # --- slow copy of the big file: one open handle, 1MB per 300ms (~6s total) ---
    $job = Start-Job -ScriptBlock {
        param($path, $total)
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        try {
            $buf = New-Object byte[] (1MB)
            $written = 0
            while ($written -lt $total) {
                $n = [Math]::Min(1MB, $total - $written)
                $fs.Write($buf, 0, $n); $fs.Flush()
                $written += $n
                Start-Sleep -Milliseconds 300
            }
        } finally { $fs.Close() }
    } -ArgumentList $bigPath, $bigSize

    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-Path $bigPath) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $bigPath)) { throw "slow writer never created the big file" }

    # --- small complete file lands while the big copy is still running ---
    'small payload' | Set-Content -Path $smallPath -Encoding ASCII

    # core assertion: small file arrives while the big copy is STILL in progress
    $deadline = (Get-Date).AddSeconds(15)
    while (-not (Test-Path (Join-Path $recvDir 'bbb_small.txt')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (-not (Test-Path (Join-Path $recvDir 'bbb_small.txt'))) { throw "FAIL: small file not sent while big file mid-copy (queue still blocked)" }
    $jobState = $job.State
    Write-Output ("PASS 1: small file sent at ~" + ([int]((Get-Date) - (Get-Item (Join-Path $recvDir 'bbb_small.txt')).CreationTime).TotalSeconds) + "s after receive; writer job state: " + $jobState)
    if ($jobState -ne 'Running') { Write-Output "note: job already finished (timing loose, rotation still implied by order below)" }

    # no partial sends: big file must NOT be in the receive dir yet
    if (Test-Path (Join-Path $recvDir 'aaa_big.bin')) { throw "FAIL: big file was sent while still being copied" }
    Write-Output "PASS 2: no half-copied file went out"

    # big file arrives only after the copy completes, and arrives complete
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path (Join-Path $recvDir 'aaa_big.bin')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 400 }
    if (-not (Test-Path (Join-Path $recvDir 'aaa_big.bin'))) { throw "FAIL: big file never arrived" }
    $got = (Get-Item (Join-Path $recvDir 'aaa_big.bin')).Length
    if ($got -ne $bigSize) { throw "FAIL: big file arrived incomplete ($got of $bigSize bytes)" }
    Write-Output ("PASS 3: big file arrived complete after copy finished ($got bytes)")

    Wait-Job $job -Timeout 10 | Out-Null
    if ($job.State -ne 'Completed') { throw "writer job did not finish" }
    if ($cli.HasExited) { Write-Output ("receiver exit code: " + $cli.ExitCode) }
    else { throw "FAIL: receiver did not exit after 2 files" }

    Write-Output "=== MONITOR SMOKE PASSED ==="
}
catch {
    Write-Output "!!! FAILURE DIAGNOSTICS !!!"
    Write-Output ("monitor dir contents: " + ((Get-ChildItem -Recurse -File $monDir -ErrorAction SilentlyContinue | ForEach-Object { $_.Name + "(" + $_.Length + ")" }) -join ", "))
    Write-Output ("recv dir contents: " + ((Get-ChildItem -Recurse -File $recvDir -ErrorAction SilentlyContinue | ForEach-Object { $_.Name + "(" + $_.Length + ")" }) -join ", "))
    if ($cli -ne $null) { Write-Output ("receiver exited: " + $cli.HasExited + " exitcode: " + $cli.ExitCode) }
    if (Test-Path (Join-Path $base 'cli_out.txt')) {
        Write-Output "--- receiver output ---"
        Write-Output (Get-Content (Join-Path $base 'cli_out.txt') -Raw)
    }
    if ($win -ne $null) {
        Write-Output "--- GUI log at failure ---"
        try { Write-Output (Get-LogText $win) } catch { Write-Output ("log read failed: " + $_.Exception.Message) }
    }
    throw
}
finally {
    if ($job -ne $null) { Remove-Job $job -Force -ErrorAction SilentlyContinue }
    if ($gui -ne $null) { Stop-Process -Id $gui.Id -Force -ErrorAction SilentlyContinue }
    if ($cli -ne $null -and -not $cli.HasExited) { Stop-Process -Id $cli.Id -Force -ErrorAction SilentlyContinue }
    Get-Process -Name 'TrFileTransfer' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($hadCfg) { Move-Item $bak $cfg -Force }
    elseif (Test-Path $cfg) { Remove-Item $cfg -Force }
    Remove-Item -Recurse -Force $base -ErrorAction SilentlyContinue
    Write-Output "cleanup done (real config restored)"
}
