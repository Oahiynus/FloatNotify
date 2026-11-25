Unicode true
RequestExecutionLevel user
ManifestDPIAware true
SetCompressor /SOLID lzma
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WordFunc.nsh"
!include "Sections.nsh"

!define App "浮笺 FloatNotify"
!define RegKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\FloatNotify"
Name "${App}"
OutFile "${OutputFile}"
InstallDir "$LOCALAPPDATA\Programs\FloatNotify"
Icon "${Stage}\SettingsUI\folio.ico"
UninstallIcon "${Stage}\SettingsUI\folio.ico"
VIProductVersion "${AppVersion}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${App}"
VIAddVersionKey /LANG=2052 "FileDescription" "浮笺 Windows x64 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${AppVersion}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${AppVersion}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright (c) 2026 Oahiynus"
ShowInstDetails show
ShowUninstDetails show
Var Registered
Var HookDir
Var ConnectArg
Var Code
Var StatusText
!define MUI_ICON "${Stage}\SettingsUI\folio.ico"
!define MUI_UNICON "${Stage}\SettingsUI\folio.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "安装浮笺 ${AppVersion}，在 Windows 桌面接收审批、问答和完成提醒。$\r$\n$\r$\n应用安装在当前用户账户下。需要 .NET 10 桌面运行时（x64）和 Microsoft Edge WebView2 运行时。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "浮笺已安装。$\r$\n$\r$\n接入 Codex 后，请在 Codex 设置中核对并信任指向本安装目录的 Stop 和 PermissionRequest。若之后才安装 Codex，可从开始菜单选择「接入 Codex」。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\FloatNotify.exe"
!define MUI_FINISHPAGE_RUN_TEXT "打开浮笺"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

!macro ReportError
    DetailPrint "$StatusText"
    FileOpen $0 "$TEMP\FloatNotify-Setup-error.log" w
    FileWriteUTF16LE /BOM $0 "Code $Code$\r$\n$StatusText$\r$\n"
    FileClose $0
!macroend

!macro UnpackSupport
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File "/oname=InstallSupport.ps1" "${Support}"
    File "/oname=inventory.txt" "${Inventory}"
!macroend

Function .onInit
    ${IfNot} ${IsNativeAMD64}
        MessageBox MB_OK|MB_ICONSTOP "此安装包适用于 Windows x64。" /SD IDOK
        SetErrorLevel 2
        Abort
    ${EndIf}
    SetRegView 64
    SetShellVarContext current
    ReadRegStr $Registered HKCU "${RegKey}" "InstallLocation"
    ${If} $Registered != ""
        StrCpy $INSTDIR "$Registered"
        ReadRegStr $0 HKCU "${RegKey}" "DisplayVersion"
        ${VersionCompare} "$0" "${AppVersion}" $1
        ${If} $1 == 1
            MessageBox MB_OK|MB_ICONSTOP "已安装的浮笺版本更新，请使用相应版本的安装包。" /SD IDOK
            SetErrorLevel 3
            Abort
        ${EndIf}
    ${EndIf}
    ReadRegStr $HookDir HKCU "${RegKey}" "HookDirectory"
    ${If} $HookDir == ""
        ReadEnvStr $HookDir "CODEX_HOME"
        ${If} $HookDir == ""
            ReadEnvStr $0 "USERPROFILE"
            StrCpy $HookDir "$0\.codex"
        ${EndIf}
    ${EndIf}
    !insertmacro UnpackSupport
    nsExec::ExecToStack '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\InstallSupport.ps1" -Action Requirements'
    Pop $Code
    Pop $StatusText
    ${If} $Code == 10
        MessageBox MB_YESNO|MB_ICONINFORMATION "请安装 Microsoft .NET 10 桌面运行时（Windows x64），再重新运行此安装包。打开微软下载页？" /SD IDNO IDNO +2
        ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/10.0"
    ${ElseIf} $Code == 11
        MessageBox MB_YESNO|MB_ICONINFORMATION "请安装 Microsoft Edge WebView2 Evergreen 运行时，再重新运行此安装包。打开微软下载页？" /SD IDNO IDNO +2
        ExecShell "open" "https://developer.microsoft.com/microsoft-edge/webview2/"
    ${ElseIf} $Code != 0
        MessageBox MB_OK|MB_ICONSTOP "无法检查运行环境。$\r$\n$StatusText" /SD IDOK
    ${EndIf}
    ${If} $Code != 0
        SetErrorLevel 4
        Abort
    ${EndIf}
FunctionEnd

Section "浮笺桌面程序（必选）" Main
    SectionIn RO
    !include "${PayloadInclude}"
    WriteUninstaller "$PLUGINSDIR\Uninstall.exe"
    StrCpy $ConnectArg ""
    SectionGetFlags 1 $0
    IntOp $0 $0 & ${SF_SELECTED}
    ${If} $0 != 0
        StrCpy $ConnectArg "-Connect"
    ${EndIf}
    DetailPrint "正在校验文件并安装浮笺…"
    nsExec::ExecToStack '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\InstallSupport.ps1" -Action Install -Destination "$INSTDIR" -RegisteredDirectory "$Registered" -HookDirectory "$HookDir" $ConnectArg'
    Pop $Code
    Pop $StatusText
    ${If} $Code != 0
        !insertmacro ReportError
        MessageBox MB_OK|MB_ICONSTOP "安装未完成。$\r$\n$StatusText" /SD IDOK
        SetErrorLevel 5
        Abort
    ${EndIf}
    ${If} $StatusText == "FLOATNOTIFY_OTHER_INSTALLATION"
        MessageBox MB_OK|MB_ICONINFORMATION "Codex 已接入其他安装位置的浮笺，原有连接已保留。如需切换，请先移除原安装的 Hook，再从开始菜单选择「接入 Codex」。" /SD IDOK
    ${EndIf}
    WriteRegStr HKCU "${RegKey}" "DisplayName" "${App}"
    WriteRegStr HKCU "${RegKey}" "DisplayVersion" "${AppVersion}"
    WriteRegStr HKCU "${RegKey}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${RegKey}" "HookDirectory" "$HookDir"
    WriteRegStr HKCU "${RegKey}" "DisplayIcon" "$INSTDIR\FloatNotify.exe"
    WriteRegStr HKCU "${RegKey}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegDWORD HKCU "${RegKey}" "NoModify" 1
    WriteRegDWORD HKCU "${RegKey}" "NoRepair" 1
    CreateDirectory "$SMPROGRAMS\FloatNotify"
    SetOutPath "$INSTDIR"
    CreateShortCut "$SMPROGRAMS\FloatNotify\浮笺.lnk" "$INSTDIR\FloatNotify.exe"
    CreateShortCut "$SMPROGRAMS\FloatNotify\接入 Codex.lnk" "$WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "$INSTDIR\Register.ps1" -ShowResult' "$INSTDIR\FloatNotify.exe"
SectionEnd
Section "接入本机 Codex（如已安装）" ConnectCodex
SectionEnd
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
    !insertmacro MUI_DESCRIPTION_TEXT ${Main} "安装桌面提醒、设置窗口及开始菜单入口。"
    !insertmacro MUI_DESCRIPTION_TEXT ${ConnectCodex} "为本用户已初始化的 Codex 添加浮笺的两个 Hook。首次信任由你在 Codex 中确认。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
    SetRegView 64
    SetShellVarContext current
    ReadRegStr $Registered HKCU "${RegKey}" "InstallLocation"
    ReadRegStr $HookDir HKCU "${RegKey}" "HookDirectory"
    !insertmacro UnpackSupport
    File "/oname=Register.ps1" "${Stage}\Register.ps1"
    nsExec::ExecToStack '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\InstallSupport.ps1" -Action Remove -Destination "$INSTDIR" -RegisteredDirectory "$Registered" -HookDirectory "$HookDir"'
    Pop $Code
    Pop $StatusText
    ${If} $Code != 0
        !insertmacro ReportError
        MessageBox MB_OK|MB_ICONSTOP "卸载未完成。$\r$\n$StatusText" /SD IDOK
        SetErrorLevel 6
        Abort
    ${EndIf}
    Delete "$SMPROGRAMS\FloatNotify\浮笺.lnk"
    Delete "$SMPROGRAMS\FloatNotify\接入 Codex.lnk"
    RMDir "$SMPROGRAMS\FloatNotify"
    DeleteRegKey HKCU "${RegKey}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR\Notices"
    RMDir "$INSTDIR\SettingsUI"
    RMDir "$INSTDIR"
SectionEnd
