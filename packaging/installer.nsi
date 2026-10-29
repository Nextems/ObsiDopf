Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!ifndef PAYLOAD
 !error "PAYLOAD must point to the published application"
!endif
!ifndef VERSION
 !define VERSION "1.0.0"
!endif
!ifndef OUTPUT
 !define OUTPUT "ObsiDopf-Setup-x64.exe"
!endif
Name "ObsiDopf"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\ObsiDopf"
InstallDirRegKey HKCU "Software\ObsidianDocs" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID zlib
SetCompressionLevel 5
BrandingText "ObsiDopf"
VIProductVersion "1.0.0.0"
VIAddVersionKey "ProductName" "ObsiDopf"
VIAddVersionKey "FileDescription" "ObsiDopf Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "ObsiDopf"
Var TestMode
!define MUI_ABORTWARNING
!define MUI_ICON "..\src\ObsidianDocs\Assets\ObsidianDocs.ico"
!define MUI_UNICON "..\src\ObsidianDocs\Assets\ObsidianDocs.ico"
!define MUI_WELCOMEPAGE_TITLE "Установка ObsiDopf"
!define MUI_WELCOMEPAGE_TEXT "Редактор PDF-комментариев, просмотр PDF и DOCX, конвертация и локальный OCR.$\r$\n$\r$\nВсе компоненты включены. Установка для текущего пользователя без прав администратора."
!define MUI_FINISHPAGE_RUN "$INSTDIR\ObsiDopf.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить ObsiDopf"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Function .onInit
 SetShellVarContext current
 ${IfNot} ${AtLeastWin10}
  MessageBox MB_ICONSTOP "Требуется Windows 10 или Windows 11."
  Abort
 ${EndIf}
 ${IfNot} ${RunningX64}
  MessageBox MB_ICONSTOP "Требуется 64-разрядная Windows."
  Abort
 ${EndIf}
 StrCpy $TestMode 0
 ${GetParameters} $0
 ClearErrors
 ${GetOptions} $0 "/TESTMODE" $1
 ${IfNot} ${Errors}
  StrCpy $TestMode 1
 ${EndIf}
FunctionEnd

Section "Программа и компоненты (обязательно)" Main
 SectionIn RO
 SetOutPath "$INSTDIR"
 ReadINIStr $0 "$INSTDIR\install.ini" "App" "Id"
 ${If} $0 == "ObsidianDocs-7F42B986-433D-472A-9FD7-2F7533D05C5A"
  Delete "$INSTDIR\ObsidianDocs.exe"
  Delete "$INSTDIR\ObsidianDocs.dll"
  Delete "$INSTDIR\ObsidianDocs.deps.json"
  Delete "$INSTDIR\ObsidianDocs.runtimeconfig.json"
  ${If} $TestMode == 0
   Delete "$SMPROGRAMS\Obsidian Docs\Obsidian Docs.lnk"
   Delete "$SMPROGRAMS\Obsidian Docs\Удалить Obsidian Docs.lnk"
   RMDir "$SMPROGRAMS\Obsidian Docs"
   Delete "$DESKTOP\Obsidian Docs.lnk"
  ${EndIf}
 ${EndIf}
 !ifdef OMIT_MANIFEST
  File /r /x "payload-manifest.json" /x "ObsidianDocs.exe" /x "ObsidianDocs.dll" /x "ObsidianDocs.deps.json" /x "ObsidianDocs.runtimeconfig.json" /x "ObsidianDocs.pdb" "${PAYLOAD}\*"
 !else
  File /r /x "ObsidianDocs.exe" /x "ObsidianDocs.dll" /x "ObsidianDocs.deps.json" /x "ObsidianDocs.runtimeconfig.json" /x "ObsidianDocs.pdb" "${PAYLOAD}\*"
 !endif
 WriteINIStr "$INSTDIR\install.ini" "App" "Id" "ObsidianDocs-7F42B986-433D-472A-9FD7-2F7533D05C5A"
 WriteINIStr "$INSTDIR\install.ini" "App" "Version" "${VERSION}"
 WriteINIStr "$INSTDIR\install.ini" "App" "TestMode" "$TestMode"
 WriteUninstaller "$INSTDIR\Uninstall.exe"
 ${If} $TestMode == 0
  WriteRegStr HKCU "Software\ObsidianDocs" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsidianDocs" "DisplayName" "ObsiDopf"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsidianDocs" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsidianDocs" "Publisher" "ObsiDopf"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsidianDocs" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsidianDocs" "DisplayIcon" "$INSTDIR\ObsiDopf.exe,0"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsidianDocs" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsidianDocs" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsidianDocs" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsidianDocs" "NoRepair" 1
  CreateDirectory "$SMPROGRAMS\ObsiDopf"
  CreateShortCut "$SMPROGRAMS\ObsiDopf\ObsiDopf.lnk" "$INSTDIR\ObsiDopf.exe"
  CreateShortCut "$SMPROGRAMS\ObsiDopf\Удалить ObsiDopf.lnk" "$INSTDIR\Uninstall.exe"
 ${EndIf}
SectionEnd

Section "Открытие PDF и DOCX через «Открыть с помощью»" FileAssociations
 ${If} $TestMode == 0
  WriteRegStr HKCU "Software\Classes\Applications\ObsiDopf.exe" "FriendlyAppName" "ObsiDopf"
  WriteRegStr HKCU "Software\Classes\Applications\ObsiDopf.exe\shell\open\command" "" '$\"$INSTDIR\ObsiDopf.exe$\" $\"%1$\"'
  WriteRegStr HKCU "Software\Classes\Applications\ObsiDopf.exe\SupportedTypes" ".pdf" ""
  WriteRegStr HKCU "Software\Classes\Applications\ObsiDopf.exe\SupportedTypes" ".docx" ""
  WriteRegStr HKCU "Software\Classes\ObsiDopf.Pdf" "" "PDF · ObsiDopf"
  WriteRegStr HKCU "Software\Classes\ObsiDopf.Pdf\DefaultIcon" "" '$\"$INSTDIR\ObsiDopf.exe$\",0'
  WriteRegStr HKCU "Software\Classes\ObsiDopf.Pdf\shell\open\command" "" '$\"$INSTDIR\ObsiDopf.exe$\" $\"%1$\"'
  WriteRegStr HKCU "Software\Classes\ObsiDopf.Docx" "" "Документ Word · ObsiDopf"
  WriteRegStr HKCU "Software\Classes\ObsiDopf.Docx\DefaultIcon" "" '$\"$INSTDIR\ObsiDopf.exe$\",0'
  WriteRegStr HKCU "Software\Classes\ObsiDopf.Docx\shell\open\command" "" '$\"$INSTDIR\ObsiDopf.exe$\" $\"%1$\"'
  WriteRegStr HKCU "Software\Classes\.pdf\OpenWithProgids" "ObsiDopf.Pdf" ""
  WriteRegStr HKCU "Software\Classes\.docx\OpenWithProgids" "ObsiDopf.Docx" ""
  WriteRegStr HKCU "Software\ObsiDopf\Capabilities" "ApplicationName" "ObsiDopf"
  WriteRegStr HKCU "Software\ObsiDopf\Capabilities" "ApplicationDescription" "Документы PDF и DOCX, комментарии и OCR"
  WriteRegStr HKCU "Software\ObsiDopf\Capabilities\FileAssociations" ".pdf" "ObsiDopf.Pdf"
  WriteRegStr HKCU "Software\ObsiDopf\Capabilities\FileAssociations" ".docx" "ObsiDopf.Docx"
  WriteRegStr HKCU "Software\RegisteredApplications" "ObsiDopf" "Software\ObsiDopf\Capabilities"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
 ${EndIf}
SectionEnd

Section /o "Ярлык на рабочем столе" DesktopShortcut
 ${If} $TestMode == 0
  CreateShortCut "$DESKTOP\ObsiDopf.lnk" "$INSTDIR\ObsiDopf.exe"
 ${EndIf}
SectionEnd

Function un.onInit
 SetShellVarContext current
 ReadINIStr $0 "$INSTDIR\install.ini" "App" "Id"
 ${If} $0 != "ObsidianDocs-7F42B986-433D-472A-9FD7-2F7533D05C5A"
  MessageBox MB_ICONSTOP "Не удалось подтвердить папку установки. Удаление отменено."
  Abort
 ${EndIf}
 ReadINIStr $TestMode "$INSTDIR\install.ini" "App" "TestMode"
FunctionEnd

Section "Uninstall"
 ${If} $TestMode == 0
  Delete "$SMPROGRAMS\ObsiDopf\ObsiDopf.lnk"
  Delete "$SMPROGRAMS\ObsiDopf\Удалить ObsiDopf.lnk"
  RMDir "$SMPROGRAMS\ObsiDopf"
  Delete "$DESKTOP\ObsiDopf.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsidianDocs"
  DeleteRegKey HKCU "Software\ObsidianDocs"
  ReadRegStr $0 HKCU "Software\Classes\Applications\ObsiDopf.exe\shell\open\command" ""
  ${If} $0 == '$\"$INSTDIR\ObsiDopf.exe$\" $\"%1$\"'
   DeleteRegKey HKCU "Software\Classes\Applications\ObsiDopf.exe"
   DeleteRegKey HKCU "Software\Classes\ObsiDopf.Pdf"
   DeleteRegKey HKCU "Software\Classes\ObsiDopf.Docx"
   DeleteRegValue HKCU "Software\Classes\.pdf\OpenWithProgids" "ObsiDopf.Pdf"
   DeleteRegValue HKCU "Software\Classes\.docx\OpenWithProgids" "ObsiDopf.Docx"
   DeleteRegValue HKCU "Software\RegisteredApplications" "ObsiDopf"
   DeleteRegKey HKCU "Software\ObsiDopf\Capabilities"
   DeleteRegKey /ifempty HKCU "Software\ObsiDopf"
   System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
  ${EndIf}
 ${EndIf}
 ; Delete only the files shipped in this installer. User-created files,
 ; author profiles and documents are never recursively removed.
 !include "uninstall-files.nsh"
 Delete "$INSTDIR\install.ini"
 Delete "$INSTDIR\Uninstall.exe"
 RMDir "$INSTDIR"
SectionEnd

