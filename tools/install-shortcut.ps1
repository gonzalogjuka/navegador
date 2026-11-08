# Crea el acceso directo "Navegador" en el escritorio apuntando a dist\Navegador.exe.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools/install-shortcut.ps1
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\Navegador.exe'
if (-not (Test-Path $exe)) { throw "No existe $exe. Corré primero: dotnet publish src/Navegador -c Release -o dist" }
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Navegador.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = Split-Path $exe
$s.IconLocation = "$exe,0"
$s.Description = 'Navegador liviano'
$s.Save()
$lnk
