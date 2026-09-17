# Makes the print agent start automatically at every logon for THIS Windows
# user, by adding an entry under HKCU's Run key.
#
# Deliberately not a Scheduled Task: registering one needs admin rights on a
# managed/corporate machine (Register-ScheduledTask fails with "Access is
# denied", HRESULT 0x80070005), which would force a UAC prompt on an
# otherwise password-free per-user install. The HKCU Run key is the standard
# per-user autostart mechanism and needs no elevation at all.
#
# It points at run-hidden.vbs rather than the exe directly so no console
# window appears at logon.
$agentDir = Split-Path -Parent $PSScriptRoot
$vbsPath = Join-Path $PSScriptRoot 'run-hidden.vbs'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'ShelivoPrintAgent'

if (-not (Test-Path $vbsPath)) {
    Write-Error "Could not find $vbsPath -- run this script from inside the agent's scripts folder, or keep the folder layout intact."
    exit 1
}

try {
    $command = "wscript.exe `"$vbsPath`""
    Set-ItemProperty -Path $runKey -Name $valueName -Value $command -ErrorAction Stop
} catch {
    Write-Error "Could not register autostart: $($_.Exception.Message)"
    exit 1
}

Write-Output "Autostart registered -- the agent will start automatically at your next logon."
Write-Output "To start it right now without logging off/on: wscript.exe `"$vbsPath`""
Write-Output "Logs (once running): $agentDir\logs\agent.log"
