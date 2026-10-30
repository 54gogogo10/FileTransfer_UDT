# Step 1 (instance A = receiver): switch to the UDP tab, verify seeded fields, start
. "$PSScriptRoot\uia_common.ps1"
$win = Get-AppWindow 13372
Write-Output ("A window: " + $win.Current.Name)

Select-Tab $win "UDP"
Start-Sleep -Milliseconds 400

# Port box should show the seeded 47781; save dir the seeded recv dir
$portOk = (Get-EditTextByValue $win "47781") -ne $null
Write-Output ("A udp-port-47781 present: " + $portOk)

$recvDir = Join-Path $env:TEMP 'gui_fec_test\recv'
$saveEdit = Get-EditTextByValue $win $recvDir
Write-Output ("A save-dir box found: " + ($saveEdit -ne $null))
if ($saveEdit -eq $null) {
    # fall back: set it wherever the seeded desktop path is
    Set-EditTextByValue $win (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders').Desktop $recvDir
} else {
    ($saveEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($recvDir)
}

Invoke-Button $win "启动"
Start-Sleep -Milliseconds 900

$log = Get-LogText $win
Write-Output ("A log has udp-started: " + ($log -match 'UDP 单向接收已启动'))
Write-Output "--- A log tail ---"
($log -split "`n" | Select-Object -Last 3) -join "`n"
