# Step 2 (instance B = sender): select UDP, verify/adjust the parity box, send
. "$PSScriptRoot\uia_common.ps1"
$win = Get-AppWindow 24692
Write-Output ("B window: " + $win.Current.Name)

Select-Radio $win "UDP"
Start-Sleep -Milliseconds 400

# The parity box shows the seeded 4; rewrite it to 3 — the send log must echo 3
$parity = Get-EditTextByValue $win "4"
Write-Output ("B parity box (value 4) found: " + ($parity -ne $null))
if ($parity -eq $null) { throw "parity box not located" }
($parity.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue("3")
Start-Sleep -Milliseconds 200
$parityAfter = Get-EditTextByValue $win "3"
Write-Output ("B parity box now reads 3: " + ($parityAfter -ne $null))

# File path and IP/port come from the seeded config — verify they are on the panel
$sendFile = Join-Path $env:TEMP 'gui_fec_test\send\fec_gui.bin'
Write-Output ("B file-path box found: " + ((Get-EditTextByValue $win $sendFile) -ne $null))
Write-Output ("B ip-127.0.0.1 box found: " + ((Get-EditTextByValue $win "127.0.0.1") -ne $null))

Invoke-Button $win "发送文件"
Write-Output "B send clicked"
Start-Sleep -Milliseconds 700

# Mid-transfer: the UDP-only params (passes/parity) must be disabled
$stillEditable = $null
try {
    $mid = Get-EditTextByValue $win "3"
    if ($mid -ne $null) {
        $enabled = $mid.Current.IsEnabled
        Write-Output ("B parity editable mid-transfer: " + $enabled + " (expect False)")
    }
} catch { Write-Output ("B mid-transfer probe skipped: " + $_.Exception.Message) }
