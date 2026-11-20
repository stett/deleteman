; Delete Man installer (NSIS 3, Modern UI 2).
;
; Per-user install, no administrator rights: installs to %LOCALAPPDATA%\Delete Man, adds a Start Menu shortcut,
; registers an uninstaller under HKCU, installs the Windows App Runtime the app is framework-dependent on (the
; runtime installer is idempotent and installs for the current user when not elevated), and adds the install
; folder to the user PATH so `deleteman` runs from any new terminal.
;
; Built by scripts\release.ps1, which stages the payload and passes three defines:
;
;   makensis /DVERSION=0.2.0 /DPAYLOAD=<abs path to artifacts\installer\payload> /DOUTFILE=<abs path to setup.exe>
;            installer\deleteman.nsi
;
; The payload folder holds the framework-dependent publish output (deleteman.exe and friends), the app-local
; VC++ runtime DLLs, path-helper\add-to-path.ps1 + remove-from-path.ps1, and WindowsAppRuntimeInstall-x64.exe.
; Everything except the runtime installer is copied to the install folder; the runtime installer is extracted
; to the temporary plug-ins folder, run once and discarded with it.
;
; Silent install:   "Delete Man_<ver>_x64-setup.exe" /S [/D=C:\some\folder]   (/D= must be last and unquoted)
; Silent uninstall: "%LOCALAPPDATA%\Delete Man\uninstall.exe" /S
;
; Relative paths below (the icon) are relative to this file: makensis changes into the script's folder.

Unicode true
ManifestDPIAware true

;--------------------------------------------------------------------------------------------------------------
; Defines passed on the command line

!ifndef VERSION
  !error "Pass the product version: /DVERSION=x.y.z"
!endif
!ifndef PAYLOAD
  !error "Pass the staged payload folder: /DPAYLOAD=<folder>"
!endif
!ifndef OUTFILE
  !error "Pass the output installer path: /DOUTFILE=<file.exe>"
!endif
!ifndef ICON
  !define ICON "..\src\DeleteMan.App\Assets\icon.ico"
!endif

!define PRODUCT_NAME "Delete Man"
!define PUBLISHER    "Steven Barnett"
!define EXE_NAME     "deleteman.exe"
!define RUNTIME_EXE  "WindowsAppRuntimeInstall-x64.exe"
!define RUNTIME_URL  "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads"
; Same key the Tauri 0.1 installer used, so installing this version over it replaces its Add/Remove Programs entry.
!define UNINST_KEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"
!define SHORTCUT     "$SMPROGRAMS\${PRODUCT_NAME}.lnk"

;--------------------------------------------------------------------------------------------------------------
; General

Name "${PRODUCT_NAME}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\${PRODUCT_NAME}"
; Re-installs and upgrades default to wherever the previous install went.
InstallDirRegKey HKCU "${UNINST_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
BrandingText "${PRODUCT_NAME} ${VERSION}"

; File properties of the setup executable. VIProductVersion needs four numeric parts.
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName"     "${PRODUCT_NAME}"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "FileVersion"     "${VERSION}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME} ${VERSION} setup"
VIAddVersionKey "CompanyName"     "${PUBLISHER}"
VIAddVersionKey "LegalCopyright"  "Copyright (c) ${PUBLISHER}"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

;--------------------------------------------------------------------------------------------------------------
; Pages

!define MUI_ICON   "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE_NAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${PRODUCT_NAME}"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

;--------------------------------------------------------------------------------------------------------------
; Install

Section "Install"
  ; The uninstaller removes the install folder recursively. If the chosen folder already holds unrelated files
  ; (for example the user picked "Documents" itself), install into a "Delete Man" subfolder instead, so that
  ; uninstalling can never take someone else's files with it. A previous Delete Man install is recognised by
  ; its executable or uninstaller and reused as is.
  ${If} ${FileExists} "$INSTDIR\*.*"
  ${AndIfNot} ${FileExists} "$INSTDIR\${EXE_NAME}"
  ${AndIfNot} ${FileExists} "$INSTDIR\uninstall.exe"
    Call IsInstDirEmpty
    Pop $0
    ${If} $0 == 0
      StrCpy $INSTDIR "$INSTDIR\${PRODUCT_NAME}"
      DetailPrint "Folder is not empty; installing into $INSTDIR"
    ${EndIf}
  ${EndIf}

  ; Copy the payload (recursively), except the runtime installer, which is only needed during setup.
  SetOutPath "$INSTDIR"
  File /r /x "${RUNTIME_EXE}" "${PAYLOAD}\*.*"

  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Start Menu shortcut. SetOutPath above makes the install folder its working directory.
  CreateShortCut "${SHORTCUT}" "$INSTDIR\${EXE_NAME}" "" "$INSTDIR\${EXE_NAME}" 0

  ; Add/Remove Programs entry (per user).
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayName"          "${PRODUCT_NAME}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   HKCU "${UNINST_KEY}" "Publisher"            "${PUBLISHER}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayIcon"          "$\"$INSTDIR\${EXE_NAME}$\""
  WriteRegStr   HKCU "${UNINST_KEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   HKCU "${UNINST_KEY}" "UninstallString"      "$\"$INSTDIR\uninstall.exe$\""
  WriteRegStr   HKCU "${UNINST_KEY}" "QuietUninstallString" "$\"$INSTDIR\uninstall.exe$\" /S"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  ; Leftover from the Tauri installer's entry; harmless but no longer accurate.
  DeleteRegValue HKCU "${UNINST_KEY}" "MainBinaryName"
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"

  ; Windows App Runtime. The app is framework-dependent on it. Microsoft's redistributable installer is
  ; idempotent (a no-op when this or a newer 2.6 runtime is present), so it runs on every install; without
  ; elevation it installs for the current user. A failure is not fatal: the files are in place and the runtime
  ; can be installed by hand later.
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "${PAYLOAD}\${RUNTIME_EXE}"
  DetailPrint "Installing the Windows App Runtime (if missing)..."
  nsExec::ExecToLog '"$PLUGINSDIR\${RUNTIME_EXE}" --quiet'
  Pop $0
  DetailPrint "Windows App Runtime installer exit code: $0"
  ${If} $0 != 0
    DetailPrint "WARNING: the Windows App Runtime could not be installed. ${PRODUCT_NAME} needs it to start."
    DetailPrint "Install the Windows App SDK 2.6 runtime (x64) from ${RUNTIME_URL}"
    ; /SD IDOK: not shown in silent mode.
    MessageBox MB_OK|MB_ICONEXCLAMATION "The Windows App Runtime installer failed (exit code $0).$\r$\n$\r$\n${PRODUCT_NAME} has been installed but needs the Windows App SDK 2.6 runtime (x64) to start. Download it from:$\r$\n${RUNTIME_URL}" /SD IDOK
  ${EndIf}
  ; Leave the plug-ins folder so NSIS can delete it on exit.
  SetOutPath "$INSTDIR"

  ; PATH, exactly as the Tauri installer hook did it.
  DetailPrint "Adding ${PRODUCT_NAME} to your PATH..."
  nsExec::ExecToLog 'powershell -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\path-helper\add-to-path.ps1" "$INSTDIR"'
  Pop $0
  DetailPrint "PATH update exit code: $0"
SectionEnd

; Pushes 1 when $INSTDIR contains no entries other than "." and "..", else 0.
Function IsInstDirEmpty
  Push $1
  Push $2
  Push $3
  StrCpy $3 1
  FindFirst $1 $2 "$INSTDIR\*.*"
  ${DoWhile} $2 != ""
    ${If} $2 != "."
    ${AndIf} $2 != ".."
      StrCpy $3 0
      ${Break}
    ${EndIf}
    FindNext $1 $2
  ${Loop}
  FindClose $1
  StrCpy $1 $3
  Pop $3
  Pop $2
  Exch $1
FunctionEnd

;--------------------------------------------------------------------------------------------------------------
; Uninstall

Section "Uninstall"
  ; Never wipe a folder that does not look like a Delete Man install (uninstall.exe copied elsewhere, or a
  ; wrong $INSTDIR): the recursive delete below would take unrelated files with it.
  ${IfNot} ${FileExists} "$INSTDIR\${EXE_NAME}"
  ${AndIfNot} ${FileExists} "$INSTDIR\path-helper\remove-from-path.ps1"
    MessageBox MB_OK|MB_ICONSTOP "$INSTDIR does not contain ${PRODUCT_NAME}. Nothing was removed." /SD IDOK
    Abort "$INSTDIR does not contain ${PRODUCT_NAME}; nothing was removed."
  ${EndIf}

  ; PATH first, while the helper script still exists.
  DetailPrint "Removing ${PRODUCT_NAME} from your PATH..."
  nsExec::ExecToLog 'powershell -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\path-helper\remove-from-path.ps1" "$INSTDIR"'
  Pop $0
  DetailPrint "PATH update exit code: $0"

  Delete "${SHORTCUT}"

  ; The install folder only ever holds what the installer put there (see the guard in the install section).
  ; Step out of it first so the uninstaller's own working directory does not keep it alive.
  SetOutPath "$TEMP"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "${UNINST_KEY}"
  ; The Windows App Runtime is shared with other apps and is deliberately left installed.
SectionEnd
