; Builds the Windows installer: registers the agent in "Installed apps"/
; Programs and Features and wires up autostart at logon, so a till only ever
; needs a double-click install rather than a copy-a-folder-and-run-a-script
; setup. Built with NSIS (see build.ps1 for the makensis invocation).
;
; Per-user install (matching the Shelivo POS app's own installer, which also
; defaults to per-user): installs under this Windows user's own LocalAppData
; and HKCU, not Program Files/HKLM, so no admin password/UAC prompt is
; needed. Only visible/installed for the Windows account that ran this
; installer -- fine for a till with one fixed login, but a till with multiple
; separate Windows logins would need this run once per account.

!define APP_NAME "Shelivo Print Agent"
!ifndef APP_VERSION
  ; Fallback for a bare `makensis shelivo-print-agent.nsi` run. build.ps1
  ; always passes /DAPP_VERSION, so that path is what real builds use.
  !define APP_VERSION "1.0.0"
!endif
!define APP_PUBLISHER "Supplyvalid"
!define APP_EXE "shelivo-print-agent.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ShelivoPrintAgent"

Unicode true

; For ${GetSize}, used in the install section to record size on disk.
!include "FileFunc.nsh"

Name "${APP_NAME}"
OutFile "..\dist\ShelivoPrintAgentSetup.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
RequestExecutionLevel user
Icon "..\assets\icon.ico"

Page directory
Page instfiles

UninstPage uninstConfirm
UninstPage instfiles

Section "Install"
  ; A running agent holds a lock on its own exe, which would otherwise fail
  ; the file copy below with "Error opening file for writing" on any
  ; reinstall/upgrade. taskkill is built into Windows; /F because the agent
  ; has no window to close gracefully, and errors are ignored since "not
  ; running" is the normal case on a first install.
  nsExec::Exec 'taskkill /F /IM ${APP_EXE}'
  Pop $0
  Sleep 500

  SetOutPath "$INSTDIR"
  File "..\dist\${APP_EXE}"

  SetOutPath "$INSTDIR\scripts"
  File "..\scripts\run-hidden.vbs"
  File "..\scripts\install-autostart.ps1"
  File "..\scripts\uninstall-autostart.ps1"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; "Installed apps" leaves the Size column blank unless EstimatedSize (in KB)
  ; is written here -- Windows measures MSI packages itself, but not an app
  ; registered through this key. Measured from what actually landed in
  ; $INSTDIR rather than hardcoded, so it stays honest as the self-contained
  ; exe grows or shrinks between releases.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"

  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1

  ; Start at every logon, via HKCU's Run key. Deliberately not a Scheduled
  ; Task: registering one needs admin rights on a managed machine (it fails
  ; with "Access is denied"), which would force a UAC prompt on this
  ; otherwise password-free per-user install. Written directly here rather
  ; than by shelling out to install-autostart.ps1, so a failure surfaces as
  ; a real installer error instead of being swallowed by a subprocess.
  ; Points at run-hidden.vbs so no console window appears at logon.
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ShelivoPrintAgent" '"$SYSDIR\wscript.exe" "$INSTDIR\scripts\run-hidden.vbs"'

  ; Also start it right now so it's usable immediately, without waiting for
  ; the next logon.
  Exec '"$SYSDIR\wscript.exe" "$INSTDIR\scripts\run-hidden.vbs"'
SectionEnd

Section "Uninstall"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ShelivoPrintAgent"

  ; Same lock problem as on install -- a running agent can't have its exe
  ; deleted out from under it.
  nsExec::Exec 'taskkill /F /IM ${APP_EXE}'
  Pop $0
  Sleep 500

  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\scripts\run-hidden.vbs"
  Delete "$INSTDIR\scripts\install-autostart.ps1"
  Delete "$INSTDIR\scripts\uninstall-autostart.ps1"
  RMDir "$INSTDIR\scripts"
  Delete "$INSTDIR\logs\agent.log"
  RMDir "$INSTDIR\logs"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  DeleteRegKey HKCU "${UNINST_KEY}"
SectionEnd
