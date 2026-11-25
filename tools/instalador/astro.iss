; Instalador de Astro (fase 8.3): AstroSetup.exe.
; Se arma con tools\instalador\armar.ps1 (publica Astro y llama a este script con ISCC). No compilar a mano.
;
; Qué hace: instala para el usuario actual, sin permisos de administrador, en %LOCALAPPDATA%\Programs\Astro; crea los
; accesos directos; queda en Configuración → Aplicaciones para desinstalar. Si a Windows le falta .NET 8 Desktop
; Runtime, lo baja de Microsoft y lo instala (eso sí pide permiso de administrador una vez: es un componente de
; Windows para todos los usuarios; el resto de la instalación no).
;
; Para pruebas y administradores (línea de comandos):
;   /VERYSILENT /SUPPRESSMSGBOXES /LOG=archivo   instalación sin ventanas, con registro
;   /DIR="carpeta"                               otra carpeta
;   /DOTNET=no                                   no revisar ni instalar .NET
;   /DOTNET=probar                               hace de cuenta que falta: lo baja, lo anota en el registro y NO lo instala
;   desinstalador: /VERYSILENT /BORRARDATOS=1    borra también los datos del usuario (favoritos, historial, modelos)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Origen
  #define Origen "salida\app"
#endif

[Setup]
AppId={{F8223511-D563-45D1-9A00-BEC61BE78DEC}
AppName=Astro
AppVersion={#AppVersion}
AppVerName=Astro {#AppVersion}
AppPublisher=Astro
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Astro
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputDir=salida
OutputBaseFilename=AstroSetup
SetupIconFile=..\..\src\Navegador\navegador.ico
UninstallDisplayIcon={app}\Navegador.exe
UninstallDisplayName=Astro
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "escritorio"; Description: "Crear un acceso directo en el escritorio"
Name: "registrar"; Description: "Que Windows ofrezca Astro como navegador (para poder elegirlo como predeterminado)"

[Files]
Source: "{#Origen}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Astro"; Filename: "{app}\Navegador.exe"; Comment: "Astro, navegador liviano"
Name: "{autodesktop}\Astro"; Filename: "{app}\Navegador.exe"; Comment: "Astro, navegador liviano"; Tasks: escritorio

[Run]
Filename: "{app}\Navegador.exe"; Parameters: "--register-default"; Flags: runhidden; Tasks: registrar
Filename: "{app}\Navegador.exe"; Description: "Abrir Astro"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  DotNetUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  DotNetFile = 'windowsdesktop-runtime-8-win-x64.exe';
  BrowserKey = 'Software\Clients\StartMenuInternet\Navegador';

var
  DownloadPage: TDownloadWizardPage;
  DeleteUserData: Boolean;

{ ---------- .NET 8 Desktop Runtime ---------- }

function HasDesktopRuntime(Root: String): Boolean;
var
  Found: TFindRec;
begin
  Result := False;
  if Root = '' then Exit;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\8.*', Found) then
  begin
    try
      repeat
        if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then Result := True;
      until Result or not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

function DotNetInstalled: Boolean;
begin
  Result := HasDesktopRuntime(ExpandConstant('{commonpf64}\dotnet')) or HasDesktopRuntime(GetEnv('DOTNET_ROOT'));
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Preparando Astro',
    'A Windows le falta un componente (.NET 8). Se baja e instala solo, una única vez.', nil);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Mode, Installer: String;
  ResultCode: Integer;
  Size: Integer;
begin
  Result := '';
  Mode := Lowercase(ExpandConstant('{param:DOTNET|auto}'));
  if Mode = 'no' then
  begin
    Log('.NET 8 Desktop Runtime: no se revisa (/DOTNET=no)');
    Exit;
  end;
  if (Mode <> 'probar') and DotNetInstalled then
  begin
    Log('.NET 8 Desktop Runtime: ya está instalado');
    Exit;
  end;

  Log('.NET 8 Desktop Runtime: falta; se baja de ' + DotNetUrl);
  DownloadPage.Clear;
  DownloadPage.Add(DotNetUrl, DotNetFile, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      Result := 'No se pudo bajar .NET 8, que Astro necesita para funcionar: ' + GetExceptionMessage + #13#10#13#10 +
        'Revisá la conexión a internet y volvé a abrir el instalador.';
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  Installer := ExpandConstant('{tmp}\' + DotNetFile);
  if Mode = 'probar' then
  begin
    if FileSize(Installer, Size) then Log(Format('.NET 8 Desktop Runtime: bajado (%d bytes). Modo prueba: no se instala.', [Size]));
    Exit;
  end;

  { El instalador de Microsoft pide permiso de administrador por su cuenta. }
  if not Exec(Installer, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    Result := 'No se pudo instalar .NET 8: ' + SysErrorMessage(ResultCode)
  else if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1641) then
    Result := Format('La instalación de .NET 8 no terminó (código %d). Astro lo necesita para funcionar: ' +
      'aceptá el pedido de permiso de Windows, o instalá ".NET 8 Desktop Runtime" desde https://dotnet.microsoft.com/download/dotnet/8.0', [ResultCode])
  else
    Log(Format('.NET 8 Desktop Runtime: instalado (código %d)', [ResultCode]));
end;

{ ---------- Desinstalar ---------- }

function AstroIsRunning: Boolean;
var
  Exe: String;
begin
  { Si Astro está abierto, su ejecutable no se puede renombrar. }
  Exe := ExpandConstant('{app}\Navegador.exe');
  Result := False;
  if not FileExists(Exe) then Exit;
  if RenameFile(Exe, Exe + '.cerrado') then
    RenameFile(Exe + '.cerrado', Exe)
  else
    Result := True;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  if AstroIsRunning then
  begin
    if not UninstallSilent then
      MsgBox('Astro está abierto. Cerralo y volvé a desinstalar.', mbInformation, MB_OK);
    Result := False;
    Exit;
  end;
  if UninstallSilent then
    DeleteUserData := ExpandConstant('{param:BORRARDATOS|0}') = '1'
  else
    DeleteUserData := MsgBox('¿Borrar también tus datos de Astro (favoritos, historial, sesión, ajustes y los modelos de voz)?' + #13#10#13#10 +
      'Si pensás volver a instalarlo, elegí No.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

function RegisteredFromHere: Boolean;
var
  Command: String;
begin
  { Sólo se quita el registro como navegador si apunta a esta instalación (puede haber otra copia de Astro). }
  Result := RegQueryStringValue(HKEY_CURRENT_USER, BrowserKey + '\shell\open\command', '', Command)
    and (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Command)) > 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegisteredFromHere then
      Exec(ExpandConstant('{app}\Navegador.exe'), '--unregister-default', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else if (CurUninstallStep = usPostUninstall) and DeleteUserData then
    DelTree(ExpandConstant('{localappdata}\Navegador'), True, True, True);
end;
