; Instalador do FieldProbe (Inno Setup 6 · https://jrsoftware.org/isinfo.php)
; Compile depois de rodar build\publish.ps1:
;     iscc build\installer.iss /DAppVersion=1.0.0

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "FieldProbe"
#define AppExe "FieldProbe.exe"
#define AppPublisher "Leonardo Cardoso"

[Setup]
AppId={{6E4C3B8A-2F1D-4E7B-9A61-3C2D5F8B1A90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=FieldProbe-Setup-{#AppVersion}
SetupIconFile=..\src\FieldProbe.App\Assets\fieldprobe.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
; LicenseFile=..\docs\EULA.txt   ; habilite quando tiver o contrato de licença (EULA)

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "fpxassoc"; Description: "Associar arquivos de projeto .fpx ao FieldProbe"; GroupDescription: "Arquivos:"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.fpx"; ValueType: string; ValueName: ""; ValueData: "FieldProbe.Project"; Flags: uninsdeletevalue; Tasks: fpxassoc
Root: HKA; Subkey: "Software\Classes\FieldProbe.Project"; ValueType: string; ValueName: ""; ValueData: "Projeto FieldProbe"; Flags: uninsdeletekey; Tasks: fpxassoc
Root: HKA; Subkey: "Software\Classes\FieldProbe.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: fpxassoc
Root: HKA; Subkey: "Software\Classes\FieldProbe.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fpxassoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
