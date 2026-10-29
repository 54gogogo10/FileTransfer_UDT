# Step 3: wait for the transfer, then verify logs, delivery and bytes
. "$PSScriptRoot\uia_common.ps1"
$recvDir = Join-Path $env:TEMP 'gui_fec_test\recv'
$sendFile = Join-Path $env:TEMP 'gui_fec_test\send\fec_gui.bin'

$delivered = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $got = Get-ChildItem $recvDir -Filter 'fec_gui*.bin' -ErrorAction SilentlyContinue
    if ($got) { $delivered = $got[0].FullName; break }
}
Write-Output ("delivered file: " + $delivered)

$winB = Get-AppWindow 24692
$logB = Get-LogText $winB
Write-Output ("B log shows FEC parity 3: " + ($logB -match '× 3 份 RS 奇偶'))
Write-Output ("B log shows send finished: " + ($logB -match '单向发送完毕: fec_gui.bin \(8.0 MB'))

$winA = Get-AppWindow 13372
$logA = Get-LogText $winA
Write-Output ("A log shows hash-verified delivery: " + ($logA -match 'hash verified|哈希校验|完整接收'))

if ($delivered) {
    $src = [System.IO.File]::ReadAllBytes($sendFile)
    $dst = [System.IO.File]::ReadAllBytes($delivered)
    Write-Output ("byte-identical: " + ((($src.Length -eq $dst.Length)) -and (-not (Compare-Object $src $dst -SyncWindow 0))))
}

# After the transfer the parity box must be enabled again
Start-Sleep -Milliseconds 3200
$parityAgain = Get-EditTextByValue $winB "3"
Write-Output ("B parity editable again: " + ($parityAgain -ne $null -and $parityAgain.Current.IsEnabled))
Write-Output "--- B log tail ---"
($logB -split "`n" | Select-Object -Last 4) -join "`n"
Write-Output "--- A log tail ---"
($logA -split "`n" | Select-Object -Last 3) -join "`n"
