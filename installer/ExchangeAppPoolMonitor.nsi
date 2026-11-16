; =====================================================================================
; Exchange AppPool Monitor installer (NSIS 3)
; Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.
;
; Builds one of two editions, chosen with FLAVOR (see "Two editions" in the README):
;   Standalone  ExchangeAppPoolMonitor-Setup-<version>-standalone.exe
;               For servers WITHOUT .NET. Installs a single exe with .NET built in. The runtime's
;               native files are unpacked by .NET on first start into the "runtime" subfolder
;               (set through the service's DOTNET_BUNDLE_EXTRACT_BASE_DIR environment variable).
;   DotNet6     ExchangeAppPoolMonitor-Setup-<version>-dotnet6.exe
;               For servers that already have the ASP.NET Core Runtime 6.0 (x64). Installs about
;               20 small files. Setup checks the runtime is installed and stops if it is not.
;
; Either edition:
;   - installs to C:\Program Files\ExchangeAppPoolMonitor by default;
;   - creates the Windows service "ExchangeAppPoolMonitor" (LocalSystem, delayed automatic
;     start, restart on failure);
;   - registers the app in Settings > Apps and in Control Panel > Programs and Features,
;     with a working Uninstall button;
;   - adds Start menu shortcuts for the status page, the settings file and the logs;
;   - asks for the main settings on two pages (email notifications, and monitoring intervals
;     and thresholds) and writes the answers into monitorsettings.json.
;
; Settings pages: on a new install they start with sensible defaults (dry run ticked). On an
; upgrade they start with the values from the existing monitorsettings.json, so pressing Next
; keeps everything as it is. Only the settings on the pages are changed; anything else in the
; file is left alone. The conversion is done by the app itself
; (ExchangeAppPoolMonitor.exe --export-settings / --import-settings, see SettingsCli.cs), using a
; small INI file in the installer's temporary folder.
; A silent install (/S) shows no pages and leaves monitorsettings.json unchanged.
;
; Upgrades: running a newer setup over an existing install stops the service, replaces the
; program files, keeps the existing monitorsettings.json and logs, and starts the service
; again if it was running before.
;
; Uninstall: stops and removes the service, removes the program files, shortcuts and the
; Apps entry, and asks whether to also delete the settings file and logs (kept by default).
;
; Silent use, for rolling out to several servers:
;   ExchangeAppPoolMonitor-Setup-1.0.0-standalone.exe /S          install (or upgrade) silently
;   ExchangeAppPoolMonitor-Setup-1.0.0-standalone.exe /S /START   ...and start the service afterwards
;   ExchangeAppPoolMonitor-Setup-1.0.0-standalone.exe /S /D=E:\Apps\AppPoolMonitor   other folder (must be last)
;   (the same switches work for the -dotnet6 setup; it exits with code 3 if the runtime is missing)
;   "C:\Program Files\ExchangeAppPoolMonitor\Uninstall.exe" /S   silent uninstall, keeps settings and logs
;
; Built by build-installer.ps1 (Windows) or build-installer.sh (Linux/macOS), which publish
; the app first and pass these values in:
;   VERSION      app version, for example 1.0.0
;   FLAVOR       Standalone or DotNet6
;   PUBLISH_DIR  folder holding that edition's publish output (plus README.md)
;   OUT_FILE     full path of the setup exe to create
;
; Supported: Windows Server 2016 (build 14393) or later, 64-bit. Needs administrator rights.
; =====================================================================================

Unicode true

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef FLAVOR
  !error "FLAVOR not defined (Standalone or DotNet6). Use build-installer.ps1 or build-installer.sh."
!endif
!if "${FLAVOR}" == "Standalone"
  !define FLAVOR_SUFFIX "standalone"
  !define FLAVOR_TEXT   "standalone edition, no .NET install needed"
!else if "${FLAVOR}" == "DotNet6"
  !define FLAVOR_SUFFIX "dotnet6"
  !define FLAVOR_TEXT   "edition for servers with the ASP.NET Core Runtime 6.0"
!else
  !error "FLAVOR must be Standalone or DotNet6, not ${FLAVOR}."
!endif
!ifndef PUBLISH_DIR
  !error "PUBLISH_DIR not defined. Use build-installer.ps1 or build-installer.sh."
!endif
!ifndef OUT_FILE
  !define OUT_FILE "ExchangeAppPoolMonitor-Setup-${VERSION}-${FLAVOR_SUFFIX}.exe"
!endif

!define APP_NAME     "Exchange AppPool Monitor"
!define AUTHOR       "Nathan Kelly"
!define COPYRIGHT    "Copyright (c) 2026 Nathan Kelly. MIT License."
!define SERVICE_NAME "ExchangeAppPoolMonitor"
!define EXE_NAME     "ExchangeAppPoolMonitor.exe"
!define SETTINGS     "monitorsettings.json"
!define UNINST_KEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\ExchangeAppPoolMonitor"
!define STARTMENU    "$SMPROGRAMS\${APP_NAME}"
; Short alias for running a PowerShell one-liner. PowerShell 5.1 is built into Server 2016+.
!define PS "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"
!include "nsDialogs.nsh"

; The installer's answers, kept in its temporary folder while it runs (deleted afterwards).
!define ANSWERS "$PLUGINSDIR\settings.ini"

Name "${APP_NAME} ${VERSION}"
OutFile "${OUT_FILE}"
InstallDir "$PROGRAMFILES64\ExchangeAppPoolMonitor"
; On an upgrade, offer the folder the previous version was installed to.
InstallDirRegKey HKLM "${UNINST_KEY}" "InstallLocation"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
BrandingText "${APP_NAME} ${VERSION}  |  ${COPYRIGHT}"

; Version information shown in the setup exe's file properties.
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName"     "${APP_NAME}"
VIAddVersionKey "FileDescription" "${APP_NAME} Setup"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "FileVersion"     "${VERSION}"
VIAddVersionKey "LegalCopyright"  "${COPYRIGHT}"
VIAddVersionKey "CompanyName"     "${AUTHOR}"

; Set during install: did the service already exist, and was it running?
Var ServiceExisted
Var ServiceWasRunning

; Settings pages: the dialog, and one handle per input box.
Var Dialog
Var hTo
Var hFrom
Var hSmtp
Var hPort
Var hSsl
Var hUser
Var hPass
Var hPoll
Var h1309
Var hQueue
Var hLong
Var hDry
; 1 when an existing settings file could not be read: the pages are skipped and the file is left alone.
Var SkipSettings

; ------------------------------------------------------------------ Pages

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "This will install ${APP_NAME} ${VERSION} (${FLAVOR_TEXT}) as a Windows service.$\r$\n$\r$\nIt watches the Exchange Autodiscover and Default app pools and recycles a pool early when requests start backing up, then logs the action and emails the people listed in its settings file.$\r$\n$\r$\nIf an earlier version is installed it will be upgraded. Your existing settings and logs are kept.$\r$\n$\r$\nClick Next to continue."
!insertmacro MUI_PAGE_WELCOME
; The MIT License the app is distributed under. It is shown for information: the license grants
; permissions and asks nothing of the user, so the button says "Next" rather than "I Agree".
!define MUI_LICENSEPAGE_TEXT_TOP "${APP_NAME} is free software by ${AUTHOR}, distributed under the MIT License."
!define MUI_LICENSEPAGE_BUTTON "&Next >"
!define MUI_LICENSEPAGE_TEXT_BOTTOM "You may use, copy, modify and share it freely, including for commercial use. It comes with no warranty."
!insertmacro MUI_PAGE_LICENSE "${PUBLISH_DIR}\LICENSE"
; After the folder is chosen, load the settings to show on the next two pages.
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE LoadSettingsForPages
!insertmacro MUI_PAGE_DIRECTORY
Page custom EmailPageCreate EmailPageLeave
Page custom MonitorPageCreate MonitorPageLeave
!insertmacro MUI_PAGE_INSTFILES

; Finish page: the settings were filled in on the pages, so "Start the service now" is ticked and
; "Open the settings file" (for the settings the pages do not cover) is not.
!define MUI_FINISHPAGE_TITLE "Installation complete"
!define MUI_FINISHPAGE_TEXT "${APP_NAME} is installed as the service $\"${SERVICE_NAME}$\". Its settings are in ${SETTINGS} in the install folder.$\r$\n$\r$\nOther settings (pool list, cooldown, log retention) can be changed later from Start menu > ${APP_NAME} > Edit settings.$\r$\n$\r$\nThe status page is at http://localhost:8085 once the service is running."
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the settings file now"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION OpenSettings
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Start the service now"
!define MUI_FINISHPAGE_RUN_FUNCTION StartServiceNow
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ------------------------------------------------------------------ Checks before installing

Function .onInit
  ; 64-bit only, and use the 64-bit registry so the Apps entry lands in the right place.
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${APP_NAME} requires 64-bit Windows." /SD IDOK
    Abort
  ${EndIf}
  SetRegView 64

  ; Windows Server 2016 is build 14393. Anything older is not supported.
  ${IfNot} ${AtLeastBuild} 14393
    MessageBox MB_ICONSTOP "${APP_NAME} requires Windows Server 2016 or later." /SD IDOK
    Abort
  ${EndIf}

!if "${FLAVOR}" == "DotNet6"
  ; This edition uses the .NET runtime installed on the server. The "ASP.NET Core Runtime 6.0"
  ; installer provides both parts it needs: Microsoft.NETCore.App and Microsoft.AspNetCore.App.
  ; Any 6.0.x patch release will do.
  FindFirst $0 $1 "$PROGRAMFILES64\dotnet\shared\Microsoft.AspNetCore.App\6.0.*"
  FindClose $0
  StrCpy $2 $1
  FindFirst $0 $1 "$PROGRAMFILES64\dotnet\shared\Microsoft.NETCore.App\6.0.*"
  FindClose $0
  ${If} $1 == ""
  ${OrIf} $2 == ""
    MessageBox MB_ICONSTOP "This edition of ${APP_NAME} needs the ASP.NET Core Runtime 6.0 (x64), which is not installed on this server.$\r$\n$\r$\nInstall $\"ASP.NET Core Runtime 6.0.x$\" (Windows x64 installer, not the Hosting Bundle) from https://dotnet.microsoft.com/download/dotnet/6.0 and run this setup again, or use the standalone edition, which needs no .NET install." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
!endif

  InitPluginsDir ; temporary folder for the settings answers file
  StrCpy $SkipSettings 0
FunctionEnd

; ------------------------------------------------------------------ Settings pages

; Runs when Next is pressed on the folder page. Fills the answers file with the values the
; settings pages should start with: this version's defaults for a new install, or the current
; values from monitorsettings.json for an upgrade.
Function LoadSettingsForPages
  StrCpy $SkipSettings 0

  ; Start a fresh answers file. Writing the UTF-16 byte order mark first makes Windows keep the
  ; file in Unicode, so addresses and passwords with non-English characters survive.
  Delete "${ANSWERS}"
  FileOpen $0 "${ANSWERS}" w
  FileWriteWord $0 0xFEFF
  FileClose $0
  WriteINIStr "${ANSWERS}" Settings To ""
  WriteINIStr "${ANSWERS}" Settings From ""
  WriteINIStr "${ANSWERS}" Settings SmtpServer "localhost"
  WriteINIStr "${ANSWERS}" Settings Port "25"
  WriteINIStr "${ANSWERS}" Settings EnableSsl "0"
  WriteINIStr "${ANSWERS}" Settings Username ""
  WriteINIStr "${ANSWERS}" Settings Password ""
  WriteINIStr "${ANSWERS}" Settings PollIntervalSeconds "10"
  WriteINIStr "${ANSWERS}" Settings Event1309IntervalSeconds "10"
  WriteINIStr "${ANSWERS}" Settings QueueThreshold "100"
  WriteINIStr "${ANSWERS}" Settings LongRunningThreshold "50"
  WriteINIStr "${ANSWERS}" Settings DryRun "1" ; new installs start in dry run mode

  ; Upgrade: replace the defaults with the current settings, read by the installed app.
  ${If} ${FileExists} "$INSTDIR\${SETTINGS}"
    ${If} ${FileExists} "$INSTDIR\${EXE_NAME}"
      nsExec::ExecToStack '"$INSTDIR\${EXE_NAME}" --export-settings "${ANSWERS}"'
      Pop $0
      Pop $1
    ${Else}
      StrCpy $0 "missing"
      StrCpy $1 "${EXE_NAME} not found"
    ${EndIf}
    ${If} $0 != 0
      ; Never overwrite settings we could not read: skip the pages and leave the file as it is.
      StrCpy $SkipSettings 1
      MessageBox MB_ICONINFORMATION "An existing ${SETTINGS} was found but could not be read ($1).$\r$\n$\r$\nThe settings pages will be skipped and the file will be left unchanged. You can edit it after installing." /SD IDOK
    ${EndIf}
  ${EndIf}
FunctionEnd

Function EmailPageCreate
  ${If} $SkipSettings == 1
    Abort ; skips the page
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "Email notifications" "Who is told when the monitor takes action, and how the email is sent."

  nsDialogs::Create 1018
  Pop $Dialog
  ${If} $Dialog == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 10u "Send notifications to (separate several addresses with ; or ,):"
  Pop $0
  ReadINIStr $1 "${ANSWERS}" Settings To
  ${NSD_CreateText} 0 11u 100% 12u $1
  Pop $hTo

  ${NSD_CreateLabel} 0 32u 70u 10u "From address:"
  Pop $0
  ReadINIStr $1 "${ANSWERS}" Settings From
  ${NSD_CreateText} 75u 30u 225u 12u $1
  Pop $hFrom

  ${NSD_CreateLabel} 0 48u 70u 10u "SMTP server:"
  Pop $0
  ReadINIStr $1 "${ANSWERS}" Settings SmtpServer
  ${NSD_CreateText} 75u 46u 150u 12u $1
  Pop $hSmtp
  ${NSD_CreateLabel} 232u 48u 20u 10u "Port:"
  Pop $0
  ReadINIStr $1 "${ANSWERS}" Settings Port
  ${NSD_CreateNumber} 255u 46u 45u 12u $1
  Pop $hPort

  ${NSD_CreateCheckbox} 75u 61u 225u 10u "Use SSL/TLS (usually with port 587)"
  Pop $hSsl
  ReadINIStr $1 "${ANSWERS}" Settings EnableSsl
  ${If} $1 == 1
    ${NSD_Check} $hSsl
  ${EndIf}

  ${NSD_CreateLabel} 0 78u 70u 10u "User name:"
  Pop $0
  ReadINIStr $1 "${ANSWERS}" Settings Username
  ${NSD_CreateText} 75u 76u 225u 12u $1
  Pop $hUser

  ${NSD_CreateLabel} 0 94u 70u 10u "Password:"
  Pop $0
  ReadINIStr $1 "${ANSWERS}" Settings Password
  ${NSD_CreatePassword} 75u 92u 225u 12u $1
  Pop $hPass

  ${NSD_CreateLabel} 0 110u 100% 24u "Leave user name and password empty for an anonymous relay, such as the Exchange server itself (localhost, port 25). A password is stored as plain text in ${SETTINGS}."
  Pop $0

  nsDialogs::Show
FunctionEnd

Function EmailPageLeave
  ${NSD_GetText} $hTo $1
  ${NSD_GetText} $hFrom $2
  ${NSD_GetText} $hSmtp $3
  ${NSD_GetText} $hPort $4

  ; Recipients: each entry must look like an address. None at all turns email off, so confirm.
  StrCpy $R0 $1
  Call ValidateEmails
  ${If} $R1 != ""
    MessageBox MB_ICONEXCLAMATION "$\"$R1$\" is not an email address."
    Abort ; stay on the page
  ${EndIf}
  ${If} $1 == ""
    MessageBox MB_ICONQUESTION|MB_YESNO "No recipients were entered, so email notifications will be turned off. Actions will still be logged.$\r$\n$\r$\nContinue without email?" IDYES +2
    Abort
  ${Else}
    ${If} $2 == ""
      MessageBox MB_ICONEXCLAMATION "Enter the From address the notifications are sent from."
      Abort
    ${EndIf}
    StrCpy $R0 $2
    Call ValidateEmails
    ${If} $R1 != ""
      MessageBox MB_ICONEXCLAMATION "The From address $\"$R1$\" is not an email address."
      Abort
    ${EndIf}
    ${If} $3 == ""
      MessageBox MB_ICONEXCLAMATION "Enter the SMTP server (for example localhost)."
      Abort
    ${EndIf}
  ${EndIf}
  ${If} $4 == ""
  ${OrIf} $4 < 1
  ${OrIf} $4 > 65535
    MessageBox MB_ICONEXCLAMATION "The SMTP port must be a number from 1 to 65535 (usually 25 or 587)."
    Abort
  ${EndIf}

  WriteINIStr "${ANSWERS}" Settings To $1
  WriteINIStr "${ANSWERS}" Settings From $2
  WriteINIStr "${ANSWERS}" Settings SmtpServer $3
  WriteINIStr "${ANSWERS}" Settings Port $4
  ${NSD_GetState} $hSsl $1
  ${If} $1 == ${BST_CHECKED}
    WriteINIStr "${ANSWERS}" Settings EnableSsl "1"
  ${Else}
    WriteINIStr "${ANSWERS}" Settings EnableSsl "0"
  ${EndIf}
  ${NSD_GetText} $hUser $1
  WriteINIStr "${ANSWERS}" Settings Username $1
  ${NSD_GetText} $hPass $1
  WriteINIStr "${ANSWERS}" Settings Password $1
FunctionEnd

Function MonitorPageCreate
  ${If} $SkipSettings == 1
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "Monitoring" "How often to check, and when to recycle a pool."

  nsDialogs::Create 1018
  Pop $Dialog
  ${If} $Dialog == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 2u 200u 10u "Check queue depth and long running requests every"
  Pop $0
  ReadINIStr $1 "${ANSWERS}" Settings PollIntervalSeconds
  ${NSD_CreateNumber} 205u 0 40u 12u $1
  Pop $hPoll
  ${NSD_CreateLabel} 250u 2u 50u 10u "seconds"
  Pop $0

  ${NSD_CreateLabel} 0 18u 200u 10u "Check for ASP.NET 1309 errors every"
  Pop $0
  ReadINIStr $1 "${ANSWERS}" Settings Event1309IntervalSeconds
  ${NSD_CreateNumber} 205u 16u 40u 12u $1
  Pop $h1309
  ${NSD_CreateLabel} 250u 18u 50u 10u "seconds"
  Pop $0

  ${NSD_CreateLabel} 0 40u 200u 10u "Recycle when the request queue is deeper than"
  Pop $0
  ReadINIStr $1 "${ANSWERS}" Settings QueueThreshold
  ${NSD_CreateNumber} 205u 38u 40u 12u $1
  Pop $hQueue
  ${NSD_CreateLabel} 250u 40u 50u 10u "requests"
  Pop $0

  ${NSD_CreateLabel} 0 56u 200u 10u "Recycle when more requests than this are 30+ s old"
  Pop $0
  ReadINIStr $1 "${ANSWERS}" Settings LongRunningThreshold
  ${NSD_CreateNumber} 205u 54u 40u 12u $1
  Pop $hLong
  ${NSD_CreateLabel} 250u 56u 50u 10u "requests"
  Pop $0

  ${NSD_CreateLabel} 0 70u 100% 10u "Either threshold must be exceeded on 2 checks in a row before a pool is recycled."
  Pop $0

  ${NSD_CreateCheckbox} 0 86u 100% 10u "Dry run: log and email what would be done, but do not recycle anything"
  Pop $hDry
  ReadINIStr $1 "${ANSWERS}" Settings DryRun
  ${If} $1 == 1
    ${NSD_Check} $hDry
  ${EndIf}
  ${NSD_CreateLabel} 12u 98u 288u 20u "Recommended for the first few days, to check the thresholds against normal traffic. Turn it off later in ${SETTINGS} ($\"DryRun$\": false)."
  Pop $0

  ${NSD_CreateLabel} 0 122u 100% 10u "All of these can be changed later from Start menu > ${APP_NAME} > Edit settings."
  Pop $0

  nsDialogs::Show
FunctionEnd

Function MonitorPageLeave
  ${NSD_GetText} $hPoll $1
  ${NSD_GetText} $h1309 $2
  ${NSD_GetText} $hQueue $3
  ${NSD_GetText} $hLong $4

  ${If} $1 == ""
  ${OrIf} $1 < 2
    MessageBox MB_ICONEXCLAMATION "The queue check interval must be at least 2 seconds."
    Abort
  ${EndIf}
  ${If} $2 == ""
  ${OrIf} $2 < 2
    MessageBox MB_ICONEXCLAMATION "The 1309 check interval must be at least 2 seconds."
    Abort
  ${EndIf}
  ${If} $3 == ""
  ${OrIf} $3 < 1
    MessageBox MB_ICONEXCLAMATION "The request queue threshold must be at least 1."
    Abort
  ${EndIf}
  ${If} $4 == ""
  ${OrIf} $4 < 1
    MessageBox MB_ICONEXCLAMATION "The long running request threshold must be at least 1."
    Abort
  ${EndIf}

  WriteINIStr "${ANSWERS}" Settings PollIntervalSeconds $1
  WriteINIStr "${ANSWERS}" Settings Event1309IntervalSeconds $2
  WriteINIStr "${ANSWERS}" Settings QueueThreshold $3
  WriteINIStr "${ANSWERS}" Settings LongRunningThreshold $4
  ${NSD_GetState} $hDry $1
  ${If} $1 == ${BST_CHECKED}
    WriteINIStr "${ANSWERS}" Settings DryRun "1"
  ${Else}
    WriteINIStr "${ANSWERS}" Settings DryRun "0"
  ${EndIf}
FunctionEnd

; Checks a list of email addresses separated by ; , or spaces.
; In:  $R0 = the list.  Out: $R1 = the first entry without an @, or "" if every entry has one.
; (The app checks the addresses again when it saves them.)
Function ValidateEmails
  Push $R2 ; position in the string
  Push $R3 ; current character
  Push $R4 ; current entry
  Push $R5 ; 1 if the current entry contains @
  StrCpy $R1 ""
  StrCpy $R2 0
  StrCpy $R4 ""
  StrCpy $R5 0
  validate_loop:
    StrCpy $R3 $R0 1 $R2
    ${If} $R3 == ""
    ${OrIf} $R3 == ";"
    ${OrIf} $R3 == ","
    ${OrIf} $R3 == " "
      ; End of an entry (or of the whole string).
      ${If} $R4 != ""
      ${AndIf} $R5 == 0
        StrCpy $R1 $R4
        Goto validate_done
      ${EndIf}
      StrCpy $R4 ""
      StrCpy $R5 0
      ${If} $R3 == ""
        Goto validate_done
      ${EndIf}
    ${Else}
      StrCpy $R4 "$R4$R3"
      ${If} $R3 == "@"
        StrCpy $R5 1
      ${EndIf}
    ${EndIf}
    IntOp $R2 $R2 + 1
    Goto validate_loop
  validate_done:
  Pop $R5
  Pop $R4
  Pop $R3
  Pop $R2
FunctionEnd

; ------------------------------------------------------------------ Shared helper

; Deletes everything in the install folder except monitorsettings.json and the logs folder.
; Used by setup before an upgrade and by the uninstaller when keeping settings and logs.
; NSIS needs a separate "un." copy of a function for the uninstaller, hence the macro.
!macro CLEAN_PROGRAM_FILES UN
Function ${UN}CleanProgramFiles
  FindFirst $0 $1 "$INSTDIR\*.*"
  ${DoWhile} $1 != ""
    ${If} $1 != "."
    ${AndIf} $1 != ".."
    ${AndIf} $1 != "${SETTINGS}"
    ${AndIf} $1 != "logs"
      ${If} ${FileExists} "$INSTDIR\$1\*.*"
        RMDir /r "$INSTDIR\$1"
      ${Else}
        Delete "$INSTDIR\$1"
      ${EndIf}
    ${EndIf}
    FindNext $0 $1
  ${Loop}
  FindClose $0
FunctionEnd
!macroend
!insertmacro CLEAN_PROGRAM_FILES ""
!insertmacro CLEAN_PROGRAM_FILES "un."

; ------------------------------------------------------------------ Install

Section "Install"
  SetShellVarContext all   ; Start menu shortcuts for all users

  ; --- 1. If the service already exists (upgrade), remember whether it was running, then stop it
  ;        so its files can be replaced. Exit codes: 2 = not installed, 1 = running, 0 = stopped.
  DetailPrint "Checking for an existing ${SERVICE_NAME} service..."
  nsExec::ExecToLog '${PS} "$$s = Get-Service ${SERVICE_NAME} -ErrorAction SilentlyContinue; if (-not $$s) { exit 2 }; if ($$s.Status -eq $\'Running$\') { exit 1 }; exit 0"'
  Pop $0
  StrCpy $ServiceExisted 0
  StrCpy $ServiceWasRunning 0
  ${If} $0 == 1
    StrCpy $ServiceExisted 1
    StrCpy $ServiceWasRunning 1
  ${ElseIf} $0 == 0
    StrCpy $ServiceExisted 1
  ${EndIf}

  ${If} $ServiceWasRunning == 1
    DetailPrint "Stopping the service for the upgrade..."
    ; Stop-Service waits until the service has actually stopped.
    nsExec::ExecToLog '${PS} "Stop-Service ${SERVICE_NAME} -Force -ErrorAction Stop"'
    Pop $0
    ${If} $0 != 0
      MessageBox MB_ICONSTOP "Could not stop the ${SERVICE_NAME} service, so its files cannot be replaced. Stop it in services.msc and run setup again." /SD IDOK
      Abort
    ${EndIf}
    Sleep 2000 ; give the process a moment to exit and release its files
  ${EndIf}

  ; --- 2. Program files. Everything from the publish folder except the settings file.
  ; Remove the previous version's program files first (keeping settings and logs), so files that
  ; are no longer used do not linger, for example when switching between the two editions. Only
  ; done when the folder really holds this app, never for some other folder chosen by mistake.
  ${If} ${FileExists} "$INSTDIR\${EXE_NAME}"
    DetailPrint "Removing the previous program files (keeping settings and logs)..."
    Call CleanProgramFiles
  ${EndIf}

  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r /x "${SETTINGS}" "${PUBLISH_DIR}\*.*"

  ; --- 3. Settings file. Never overwrite one that is already there: it holds the admin's SMTP
  ;        details and thresholds. A copy of this version's defaults is saved alongside it
  ;        (monitorsettings.default.json) so new settings can be compared after an upgrade.
  ${IfNot} ${FileExists} "$INSTDIR\${SETTINGS}"
    File "${PUBLISH_DIR}\${SETTINGS}"
  ${Else}
    DetailPrint "Keeping the existing ${SETTINGS}."
  ${EndIf}
  File "/oname=monitorsettings.default.json" "${PUBLISH_DIR}\${SETTINGS}"

  ; --- 3b. Write the answers from the settings pages into the settings file. Skipped for a silent
  ;         install (no pages were shown) and when the existing file could not be read.
  ${IfNot} ${Silent}
  ${AndIf} $SkipSettings != 1
    DetailPrint "Saving your settings to ${SETTINGS}..."
    nsExec::ExecToStack '"$INSTDIR\${EXE_NAME}" --import-settings "${ANSWERS}"'
    Pop $0
    Pop $1
    ${If} $0 != 0
      DetailPrint "Could not save the settings: $1"
      MessageBox MB_ICONEXCLAMATION "Your settings could not be saved to ${SETTINGS}:$\r$\n$\r$\n$1$\r$\nThe installation will continue. Edit the file afterwards from Start menu > ${APP_NAME} > Edit settings."
    ${EndIf}
  ${EndIf}

  ; --- 4. The Windows service. sc.exe needs the space after each "=". The path is quoted
  ;        because it contains spaces ("Program Files").
  ${If} $ServiceExisted == 0
    DetailPrint "Creating the ${SERVICE_NAME} service..."
    nsExec::ExecToLog 'sc.exe create ${SERVICE_NAME} binPath= "\"$INSTDIR\${EXE_NAME}\"" start= delayed-auto DisplayName= "${APP_NAME}"'
  ${Else}
    DetailPrint "Updating the ${SERVICE_NAME} service..."
    ; Point it at the (possibly new) install folder and make sure the start type is right.
    nsExec::ExecToLog 'sc.exe config ${SERVICE_NAME} binPath= "\"$INSTDIR\${EXE_NAME}\"" start= delayed-auto DisplayName= "${APP_NAME}"'
  ${EndIf}
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "Could not create or update the ${SERVICE_NAME} service (sc.exe exit code $0). See the details list." /SD IDOK
    Abort
  ${EndIf}
  nsExec::ExecToLog 'sc.exe description ${SERVICE_NAME} "Recycles Exchange app pools early when requests back up or ASP.NET 1309 errors appear."'
  Pop $0
  ; Restart the service 1 minute after a crash, up to three times a day.
  nsExec::ExecToLog 'sc.exe failure ${SERVICE_NAME} reset= 86400 actions= restart/60000/restart/60000/restart/60000'
  Pop $0
  ; Also restart it when it stops with an error without crashing (the service reports an error
  ; code when it stops without having been asked to), not only when the process crashes.
  nsExec::ExecToLog 'sc.exe failureflag ${SERVICE_NAME} 1'
  Pop $0

!if "${FLAVOR}" == "Standalone"
  ; The single exe unpacks a few native runtime files on first start. By default that is the
  ; account's temp folder (C:\Windows\Temp for LocalSystem), where cleanup tools may remove them.
  ; Point it at the "runtime" subfolder instead, through the service's own environment.
  DetailPrint "Setting the runtime folder for the service..."
  nsExec::ExecToLog '${PS} "New-ItemProperty -Path $\'HKLM:\SYSTEM\CurrentControlSet\Services\${SERVICE_NAME}$\' -Name Environment -PropertyType MultiString -Value $\'DOTNET_BUNDLE_EXTRACT_BASE_DIR=$INSTDIR\runtime$\' -Force | Out-Null"'
  Pop $0
!else
  ; Not needed by this edition; remove it in case the standalone edition was installed before.
  nsExec::ExecToLog '${PS} "Remove-ItemProperty -Path $\'HKLM:\SYSTEM\CurrentControlSet\Services\${SERVICE_NAME}$\' -Name Environment -ErrorAction SilentlyContinue"'
  Pop $0
!endif

  ; --- 5. Start menu shortcuts.
  CreateDirectory "${STARTMENU}"
  WriteINIStr "${STARTMENU}\Status page.url" "InternetShortcut" "URL" "http://localhost:8085/"
  ; The settings file is under Program Files, which only an elevated program can save to, so this
  ; shortcut opens Notepad "as administrator" (one UAC prompt) via a hidden PowerShell window.
  CreateShortcut "${STARTMENU}\Edit settings (monitorsettings.json).lnk" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" \
    '-NoProfile -WindowStyle Hidden -Command "Start-Process notepad.exe -Verb RunAs -ArgumentList $\'\"$INSTDIR\${SETTINGS}\"$\'"' \
    "$WINDIR\notepad.exe" 0 SW_SHOWMINIMIZED
  CreateDirectory "$INSTDIR\logs"
  CreateShortcut "${STARTMENU}\Logs folder.lnk" "$INSTDIR\logs"
  CreateShortcut "${STARTMENU}\README.lnk" "$WINDIR\notepad.exe" '"$INSTDIR\README.md"'
  CreateShortcut "${STARTMENU}\License (MIT).lnk" "$WINDIR\notepad.exe" '"$INSTDIR\LICENSE"'
  CreateShortcut "${STARTMENU}\Runbook (operations guide).lnk" "$WINDIR\notepad.exe" '"$INSTDIR\docs\RUNBOOK.md"'
  CreateShortcut "${STARTMENU}\Maintainer's guide.lnk" "$WINDIR\notepad.exe" '"$INSTDIR\docs\MAINTAINING.md"'
  CreateShortcut "${STARTMENU}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"

  ; --- 6. Uninstaller and the Apps / Programs and Features entry.
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr   HKLM "${UNINST_KEY}" "DisplayName"          "${APP_NAME}"
  WriteRegStr   HKLM "${UNINST_KEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   HKLM "${UNINST_KEY}" "Publisher"            "${AUTHOR}"
  WriteRegStr   HKLM "${UNINST_KEY}" "DisplayIcon"          "$INSTDIR\${EXE_NAME}"
  WriteRegStr   HKLM "${UNINST_KEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   HKLM "${UNINST_KEY}" "UninstallString"      '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr   HKLM "${UNINST_KEY}" "Comments"             "Windows service (${FLAVOR_TEXT}). Status page: http://localhost:8085"
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
  ; Size shown in Apps, in KB.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$0"

  ; --- 7. Start the service again if it was running before the upgrade, or if /START was given.
  ${GetParameters} $1
  ClearErrors
  ${GetOptions} $1 "/START" $2
  ${If} $ServiceWasRunning == 1
  ${OrIfNot} ${Errors}
    Call StartServiceNow
  ${EndIf}
SectionEnd

; ------------------------------------------------------------------ Finish page actions

Function StartServiceNow
  DetailPrint "Starting the ${SERVICE_NAME} service..."
  nsExec::ExecToLog '${PS} "Start-Service ${SERVICE_NAME} -ErrorAction Stop"'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION "The service was installed but did not start. Check the newest file in $INSTDIR\logs\ (service-*.log)." /SD IDOK
  ${EndIf}
FunctionEnd

Function OpenSettings
  Exec '"$WINDIR\notepad.exe" "$INSTDIR\${SETTINGS}"'
FunctionEnd

; ------------------------------------------------------------------ Uninstall

Function un.onInit
  SetRegView 64
FunctionEnd

Section "Uninstall"
  SetShellVarContext all

  ; --- 1. Stop and remove the service. Errors are ignored: it may already be stopped or gone.
  DetailPrint "Stopping and removing the ${SERVICE_NAME} service..."
  nsExec::ExecToLog '${PS} "Stop-Service ${SERVICE_NAME} -Force -ErrorAction SilentlyContinue"'
  Pop $0
  nsExec::ExecToLog 'sc.exe delete ${SERVICE_NAME}'
  Pop $0
  Sleep 2000 ; let the process exit and release its files

  ; --- 2. Settings and logs: keep them unless the admin says otherwise. Silent uninstall keeps them.
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Also delete the settings file (${SETTINGS}) and all logs in $INSTDIR\logs?$\r$\n$\r$\nChoose No to keep them, for example to reinstall later." /SD IDNO IDYES deleteAll

  ; Keep: delete everything in the install folder except the settings file and the logs folder.
  Call un.CleanProgramFiles
  DetailPrint "Kept $INSTDIR\${SETTINGS} and $INSTDIR\logs."
  Goto removeRest

deleteAll:
  RMDir /r "$INSTDIR"

removeRest:
  ; --- 3. Shortcuts and the Apps entry.
  RMDir /r "${STARTMENU}"
  DeleteRegKey HKLM "${UNINST_KEY}"
SectionEnd
