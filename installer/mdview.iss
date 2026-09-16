#define AppName "mdview"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{7B6BF47B-8EA5-4F15-87C7-A7FF2B61A1D4}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=GaspsInSpanish
AppMutex=Local\mdview-singleton
DefaultDirName={localappdata}\Programs\mdview
DefaultGroupName=mdview
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ChangesAssociations=yes
OutputDir=Output
OutputBaseFilename=mdview-setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\mdview.exe
InfoAfterFile=POSTINSTALL.txt

[Files]
Source: "..\src\MdView\bin\Release\net8.0-windows\win-x64\publish\mdview.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\mdview"; Filename: "{app}\mdview.exe"

[Tasks]
Name: "associate"; Description: "Make mdview the default app for .md files"; Flags: checkedonce

[Registry]
Root: HKCU; Subkey: "Software\Classes\mdview.md"; ValueType: string; ValueName: ""; ValueData: "mdview Markdown Document"; Tasks: associate; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\mdview.md\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\mdview.exe"",0"; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\mdview.md\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\mdview.exe"" ""%1"""; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: none; ValueName: "mdview.md"; Tasks: associate; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\mdview\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "mdview"; Tasks: associate; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\mdview\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A focused Markdown reader"; Tasks: associate
Root: HKCU; Subkey: "Software\mdview\Capabilities\FileAssociations"; ValueType: string; ValueName: ".md"; ValueData: "mdview.md"; Tasks: associate
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "mdview"; ValueData: "Software\mdview\Capabilities"; Tasks: associate; Flags: uninsdeletevalue

[Run]
Filename: "ms-settings:defaultapps"; Description: "Open Default Apps to select mdview for .md files"; Flags: postinstall shellexec unchecked
