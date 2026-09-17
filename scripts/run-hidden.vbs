' Launches the print agent with no visible console window, logging stdout/
' stderr to logs\agent.log since there's no terminal to watch once this runs
' unattended at logon. This is what the autostart entry points at.
Set objShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
agentDir = fso.GetParentFolderName(scriptDir)
logDir = agentDir & "\logs"
exePath = agentDir & "\shelivo-print-agent.exe"

If Not fso.FolderExists(logDir) Then
  fso.CreateFolder(logDir)
End If

If Not fso.FileExists(exePath) Then
  WScript.Quit 1
End If

' cmd needs the whole command wrapped in an extra pair of quotes when it
' starts with a quoted path -- otherwise it reads the leading quote as the
' start of a window title and fails with "is not recognized as an internal
' or external command".
q = Chr(34)
cmd = "cmd /c " & q & q & exePath & q & " >> " & q & logDir & "\agent.log" & q & " 2>&1" & q

objShell.Run cmd, 0, False
