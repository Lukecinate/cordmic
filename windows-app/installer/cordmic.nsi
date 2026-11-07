; CordMic installer (NSIS 3, Unicode). Build:  makensis -V2 -DVERSION=x.y.z cordmic.nsi
; Input: ../publish (dotnet publish win-x64 single-file) and tools/*.ps1

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!define APPNAME   "CordMic"
!ifndef VERSION
  !define VERSION "0.5.3"
!endif
!define PUBLISHER "CordMic contributors"
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
VIAddVersionKey /LANG=0 "LegalCopyright" "Copyright (c) ${PUBLISHER}. MIT License."

!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"

!define MUI_ICON   "..\src\CordMic.App\Assets\cordmic.ico"
!define MUI_UNICON "..\src\CordMic.App\Assets\cordmic.ico"
!define MUI_ABORTWARNING
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "art\header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "art\header.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP "art\welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "art\welcome.bmp"
!define MUI_COMPONENTSPAGE_NODESC

!define MUI_WELCOMEPAGE_TITLE "$(TxtWelcomeTitle)"
!define MUI_WELCOMEPAGE_TEXT "$(TxtWelcomeText)"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "$(TxtFinishTitle)"
!define MUI_FINISHPAGE_TEXT "$(TxtFinishText)"
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "$(TxtFinishRun)"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchUnelevated
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; English is the default; Indonesian is picked automatically when the Windows UI language is Indonesian.
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Indonesian"

LangString TxtWelcomeTitle ${LANG_ENGLISH} "Install CordMic"
LangString TxtWelcomeTitle ${LANG_INDONESIAN} "Pasang CordMic"
LangString TxtWelcomeText ${LANG_ENGLISH} "CordMic turns an Android phone into a PC microphone over a USB cable (USB tethering), with no Wi-Fi and no USB debugging.$\r$\n$\r$\nThis installer will:$\r$\n  • install the CordMic tray app$\r$\n  • open UDP port 50005 in the firewall (local network only)$\r$\n  • give the phone adapter a low priority so games keep using LAN/Wi-Fi$\r$\n$\r$\nThe virtual mic (VB-CABLE) is installed separately from its official site; a setup guide opens after installation."
LangString TxtWelcomeText ${LANG_INDONESIAN} "CordMic menjadikan HP Android sebagai mikrofon PC lewat kabel USB (USB tethering), tanpa Wi-Fi dan tanpa USB debugging.$\r$\n$\r$\nInstaller ini akan:$\r$\n  • memasang aplikasi tray CordMic$\r$\n  • membuka port UDP 50005 di firewall (hanya jaringan lokal)$\r$\n  • membuat adapter HP berprioritas rendah agar game tetap pakai LAN/Wi-Fi$\r$\n$\r$\nMic virtual (VB-CABLE) dipasang terpisah dari situs resminya; panduan akan muncul setelah instalasi."
LangString TxtFinishTitle ${LANG_ENGLISH} "CordMic is installed"
LangString TxtFinishTitle ${LANG_INDONESIAN} "CordMic sudah terpasang"
LangString TxtFinishText ${LANG_ENGLISH} "CordMic runs in the system tray (bottom right). The setup guide opens when the app starts."
LangString TxtFinishText ${LANG_INDONESIAN} "CordMic berjalan di tray (pojok kanan bawah). Panduan penyiapan akan terbuka saat aplikasi dijalankan."
LangString TxtFinishRun ${LANG_ENGLISH} "Run CordMic now"
LangString TxtFinishRun ${LANG_INDONESIAN} "Jalankan CordMic sekarang"
LangString TxtNeed64 ${LANG_ENGLISH} "CordMic requires 64-bit Windows."
LangString TxtNeed64 ${LANG_INDONESIAN} "CordMic membutuhkan Windows 64-bit."
LangString TxtRemoveLegacy ${LANG_ENGLISH} "Removing the old ${LEGACYNAME} installation…"
LangString TxtRemoveLegacy ${LANG_INDONESIAN} "Menghapus instalasi lama ${LEGACYNAME}…"
LangString TxtClosing ${LANG_ENGLISH} "Closing running CordMic…"
LangString TxtClosing ${LANG_INDONESIAN} "Menutup CordMic yang sedang berjalan…"
LangString TxtShortcutDesc ${LANG_ENGLISH} "Use your phone as a PC microphone over USB"
LangString TxtShortcutDesc ${LANG_INDONESIAN} "HP sebagai mikrofon PC lewat USB"
LangString TxtConfigure ${LANG_ENGLISH} "Configuring the firewall (UDP 50005) and phone adapter priority…"
LangString TxtConfigure ${LANG_INDONESIAN} "Mengatur firewall (UDP 50005) dan prioritas adapter HP…"
LangString TxtPsFailed ${LANG_ENGLISH} "PowerShell setup failed ($0). Using netsh for the firewall."
LangString TxtPsFailed ${LANG_INDONESIAN} "PowerShell setup gagal ($0). Memakai netsh untuk firewall."
LangString SecMainName ${LANG_ENGLISH} "CordMic (required)"
LangString SecMainName ${LANG_INDONESIAN} "CordMic (wajib)"
LangString SecStartupName ${LANG_ENGLISH} "Start automatically with Windows"
LangString SecStartupName ${LANG_INDONESIAN} "Jalan otomatis saat Windows mulai"
LangString SecDesktopName ${LANG_ENGLISH} "Desktop shortcut"
LangString SecDesktopName ${LANG_INDONESIAN} "Shortcut di Desktop"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "$(TxtNeed64)"
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
  DetailPrint "$(TxtRemoveLegacy)"
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

Section "$(SecMainName)" SecMain
  SectionIn RO
  DetailPrint "$(TxtClosing)"
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
  CreateShortcut "$SMPROGRAMS\CordMic.lnk" "$INSTDIR\CordMic.exe" "" "$INSTDIR\CordMic.exe" 0 SW_SHOWNORMAL "" "$(TxtShortcutDesc)"

  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayName" "CordMic"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr   HKLM "${UNINSTKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\CordMic.exe"
  WriteRegStr   HKLM "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" 150000

  DetailPrint "$(TxtConfigure)"
  nsExec::ExecToLog '"${PS}" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\tools\cordmic-setup.ps1" -Action Install -InstallDir "$INSTDIR\tools"'
  Pop $0
  ${If} $0 != 0
    DetailPrint "$(TxtPsFailed)"
    nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall add rule name="CordMic (UDP 50005)" dir=in action=allow protocol=UDP localport=50005 program="$INSTDIR\CordMic.exe" remoteip=localsubnet profile=any'
    Pop $0
  ${EndIf}

  DetailPrint "Optimizing VB-CABLE (48 kHz stereo, enhancements off) if it is installed..."
  nsExec::ExecToLog '"$INSTDIR\CordMic.exe" --configure-cable'
  Pop $0
  ${If} $0 != 0
  ${AndIf} $0 != 2
    DetailPrint "VB-CABLE optimize skipped or failed (exit $0). CordMic will still work."
  ${EndIf}
SectionEnd

Section "$(SecStartupName)" SecStartup
  WriteRegStr HKCU "${RUNKEY}" "CordMic" '"$INSTDIR\CordMic.exe" --tray'
SectionEnd

Section /o "$(SecDesktopName)" SecDesktop
  SetShellVarContext current
  CreateShortcut "$DESKTOP\CordMic.lnk" "$INSTDIR\CordMic.exe"
SectionEnd

Section "Uninstall"
  nsExec::Exec '"$SYSDIR\taskkill.exe" /IM CordMic.exe /F'
  Pop $0
  Sleep 700
  DetailPrint "Restoring VB-CABLE device format if CordMic changed it..."
  nsExec::ExecToLog '"$INSTDIR\CordMic.exe" --restore-cable'
  Pop $0
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
