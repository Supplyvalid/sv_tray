# Removes the autostart entry install-autostart.ps1 creates. Does not stop an
# already-running agent -- close it separately (Task Manager) if needed.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'ShelivoPrintAgent'

$existing = Get-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue
if ($existing) {
    Remove-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue
    Write-Output "Autostart entry removed."
} else {
    Write-Output "No autostart entry found -- nothing to remove."
}
