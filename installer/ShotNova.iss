; Instalador do ShotNova (Inno Setup 6). Compile com: tools\build-installer.ps1
#define MyAppName "ShotNova"
#define MyAppVersion "0.3.0"
#define MyAppExe "ShotNova.exe"

[Setup]
AppId={{16DFAB91-FE7F-41DF-9E99-9D3D0CCE3F9E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=ShotNova
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\installer-out
OutputBaseFilename=ShotNova-Setup-{#MyAppVersion}
SetupIconFile=..\assets\ShotNova.ico
UninstallDisplayIcon={app}\{#MyAppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex=ShotNova.Single
CloseApplications=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; Flags: unchecked
Name: "autostart"; Description: "Iniciar o ShotNova com o Windows"; Flags: unchecked
Name: "shellmenu"; Description: "Adicionar ao menu de contexto (Editar imagem / Capturar área). No Windows 11 aparece em ""Mostrar mais opções"""; Flags: unchecked

[Files]
Source: "..\dist\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExe}"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\image\shell\ShotNova"; ValueType: string; ValueData: "Editar no ShotNova"; Tasks: shellmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\image\shell\ShotNova"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExe}"; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\image\shell\ShotNova\command"; ValueType: string; ValueData: """{app}\{#MyAppExe}"" ""%1"""; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\ShotNovaCapture"; ValueType: string; ValueData: "Capturar área com ShotNova"; Tasks: shellmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\ShotNovaCapture"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExe}"; Tasks: shellmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\ShotNovaCapture\command"; ValueType: string; ValueData: """{app}\{#MyAppExe}"" --capture"; Tasks: shellmenu

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Abrir o ShotNova agora"; Flags: nowait postinstall skipifsilent

[Code]
// O ShotNova roda sobre o .NET Desktop Runtime 10 (instalado separadamente).
function DesktopRuntime10Installed(): Boolean;
var
  R: TFindRec;
  Base: String;
begin
  Result := False;
  Base := ExpandConstant('{commonpf64}') + '\dotnet\shared\Microsoft.WindowsDesktop.App\';
  if FindFirst(Base + '10.*', R) then
  begin
    Result := True;
    FindClose(R);
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrCode: Integer;
begin
  Result := True;
  if not DesktopRuntime10Installed() then
  begin
    if MsgBox('O ShotNova precisa do ".NET Desktop Runtime 10", que não foi encontrado neste computador.' + #13#10#13#10 +
              'Abrir a página de download agora? (Depois de instalar o Runtime, execute este instalador de novo.)',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
      Result := False;
    end;
  end;
end;

// Ao desinstalar, remove também as integrações que o usuário ativou pela bandeja do app.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\SystemFileAssociations\image\shell\ShotNova');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\Background\shell\ShotNovaCapture');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ShotNova');
  end;
end;
