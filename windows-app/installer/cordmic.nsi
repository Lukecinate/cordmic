; CordMic installer (NSIS 3, Unicode). Build on Linux:  makensis -V2 cordmic.nsi
; Input: ../publish (dotnet publish win-x64 single-file) and tools/*.ps1

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!define APPNAME   "CordMic"
!ifndef VERSION
  !define VERSION "0.3.0"
!endif
!define PUBLISHER "In-house"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\CordMic"
!define RUNKEY    "Software\Microsoft\Windows\CurrentVersion\Run"
; Pre-0.3.0 product name (upgrade path: the old install is removed cleanly, user settings are kept).
!define LEGACYNAME "UsbMic"
!define LEGACYKEY  "Software\Microsoft\Windows\CurrentVersion\Uninstall\UsbMic"
!define PS        "$WINDIR\sysnative\WindowsPowerShell\v1.0\powershell.exe"

Name "${APPNAME} ${VERSION}"
OutFile "..\..\dist\CordMic-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\CordMic"
InstallDirRegKey HKLM "${UNINSTKEY}" "InstallLocation"
RequestExecutionLevel admin
BrandingText "CordMic ${VERSION}"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=0 "ProductName" "CordMic"
VIAddVersionKey /LANG=0 "FileDescription" "CordMic Setup"
VIAddVersionKey /LANG=0 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=0 "LegalCopyright" "${PUBLISHER}"

!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"

!define MUI_ICON   "..\src\CordMic.App\Assets\cordmic.ico"
!define MUI_UNICON "..\src\CordMic.App\Assets\cordmic.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_NODESC

!define MUI_WELCOMEPAGE_TITLE "Pasang CordMic"
!define MUI_WELCOMEPAGE_TEXT "CordMic menjadikan HP Android sebagai mikrofon PC lewat kabel USB (USB tethering), tanpa Wi-Fi dan tanpa USB debugging.$\r$\n$\r$\nInstaller ini akan:$\r$\n  • memasang aplikasi tray CordMic$\r$\n  • membuka port UDP 50005 di firewall (hanya jaringan lokal)$\r$\n  • membuat adapter HP berprioritas rendah agar game tetap pakai LAN/Wi-Fi$\r$\n$\r$\nMic virtual (VB-CABLE) dipasang terpisah dari situs resminya; panduan akan muncul setelah instalasi."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "CordMic sudah terpasang"
!define MUI_FINISHPAGE_TEXT "CordMic berjalan di tray (pojok kanan bawah). Panduan penyiapan akan terbuka saat aplikasi dijalankan."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Jalankan CordMic sekarang"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchUnelevated
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Indonesian"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "CordMic membutuhkan Windows 64-bit."
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

Function un.onInit
  SetRegView 64
FunctionEnd

; Start the tray app as the logged-in user (not elevated): explorer.exe launches it with the shell's token.
Function LaunchUnelevated
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\CordMic.exe"'
FunctionEnd

;  Upgrade from the old brand: stop the old exe, remove its firewall rule, scheduled task, autostart value,
;  shortcuts, program files and uninstall entry. %APPDATA%\${LEGACYNAME}\settings.json is NOT deleted:
;  CordMic copies it to %APPDATA%\CordMic on first start.
Function RemoveLegacyUsbMic
  ReadRegStr $1 HKLM "${LEGACYKEY}" "InstallLocation"
  ${If} $1 == ""
  ${AndIf} ${FileExists} "$PROGRAMFILES64\${LEGACYNAME}\${LEGACYNAME}.exe"
    StrCpy $1 "$PROGRAMFILES64\${LEGACYNAME}"
  ${EndIf}
  ReadRegStr $2 HKCU "${RUNKEY}" "${LEGACYNAME}"
  ${If} $1 == ""
  ${AndIf} $2 == ""
    Return
  ${EndIf}
  DetailPrint "Menghapus instalasi lama ${LEGACYNAME}…"
  nsExec::Exec '"$SYSDIR\taskkill.exe" /IM ${LEGACYNAME}.exe /F'
  Pop $0
  Sleep 700
  nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="${LEGACYNAME} (UDP 50005)"'
  Pop $0
  nsExec::ExecToLog '"$SYSDIR\schtasks.exe" /Delete /TN "\${LEGACYNAME}\SetTetherMetric" /F'
  Pop $0
  DeleteRegValue HKCU "${RUNKEY}" "${LEGACYNAME}"
  SetShellVarContext all
  Delete "$SMPROGRAMS\${LEGACYNAME}.lnk"
  ReadEnvStr $3 "PROGRAMDATA"
  ${If} $3 != ""
    RMDir /r "$3\${LEGACYNAME}"  ; machine-wide setup/metric logs only (user settings live in %APPDATA%)
  ${EndIf}
  SetShellVarContext current
  Delete "$DESKTOP\${LEGACYNAME}.lnk"
  ${If} $1 != ""
  ${AndIf} $1 != "$INSTDIR"
    RMDir /r "$1\tools"
    Delete "$1\${LEGACYNAME}.exe"
    Delete "$1\Uninstall.exe"
    Delete "$1\*.dll"
    RMDir "$1"
  ${EndIf}
  DeleteRegKey HKLM "${LEGACYKEY}"
FunctionEnd

Section "CordMic (wajib)" SecMain
  SectionIn RO
  DetailPrint "Menutup CordMic yang sedang berjalan…"
  nsExec::Exec '"$SYSDIR\taskkill.exe" /IM CordMic.exe /F'
  Pop $0
  Sleep 700

  Call RemoveLegacyUsbMic

  SetOutPath "$INSTDIR"
  File /r /x "*.pdb" "..\publish\*.*"
  SetOutPath "$INSTDIR\tools"
  File "tools\set-tether-metric.ps1"
  File "tools\cordmic-setup.ps1"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  SetShellVarContext all
  CreateShortcut "$SMPROGRAMS\CordMic.lnk" "$INSTDIR\CordMic.exe" "" "$INSTDIR\CordMic.exe" 0 SW_SHOWNORMAL "" "HP sebagai mikrofon PC lewat USB"

  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayName" "CordMic"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr   HKLM "${UNINSTKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\CordMic.exe"
  WriteRegStr   HKLM "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" 150000

  DetailPrint "Mengatur firewall (UDP 50005) dan prioritas adapter HP…"
  nsExec::ExecToLog '"${PS}" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\tools\cordmic-setup.ps1" -Action Install -InstallDir "$INSTDIR\tools"'
  Pop $0
  ${If} $0 != 0
    DetailPrint "PowerShell setup gagal ($0). Memakai netsh untuk firewall."
    nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall add rule name="CordMic (UDP 50005)" dir=in action=allow protocol=UDP localport=50005 program="$INSTDIR\CordMic.exe" remoteip=localsubnet profile=any'
    Pop $0
  ${EndIf}
SectionEnd

Section "Jalan otomatis saat Windows mulai" SecStartup
  WriteRegStr HKCU "${RUNKEY}" "CordMic" '"$INSTDIR\CordMic.exe" --tray'
SectionEnd

Section /o "Shortcut di Desktop" SecDesktop
  SetShellVarContext current
  CreateShortcut "$DESKTOP\CordMic.lnk" "$INSTDIR\CordMic.exe"
SectionEnd

Section "Uninstall"
  nsExec::Exec '"$SYSDIR\taskkill.exe" /IM CordMic.exe /F'
  Pop $0
  Sleep 700
  nsExec::ExecToLog '"${PS}" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\tools\cordmic-setup.ps1" -Action Uninstall'
  Pop $0
  nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="CordMic (UDP 50005)"'
  Pop $0

  DeleteRegValue HKCU "${RUNKEY}" "CordMic"
  DeleteRegKey HKLM "${UNINSTKEY}"
  SetShellVarContext all
  Delete "$SMPROGRAMS\CordMic.lnk"
  SetShellVarContext current
  Delete "$DESKTOP\CordMic.lnk"

  RMDir /r "$INSTDIR\tools"
  Delete "$INSTDIR\*.exe"
  Delete "$INSTDIR\*.dll"
  RMDir "$INSTDIR"
  ; Settings (%APPDATA%\CordMic) and logs (%LOCALAPPDATA%\CordMic) are kept on purpose.
SectionEnd
