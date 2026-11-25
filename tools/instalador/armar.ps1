# Arma el instalador de Astro (fase 8.3): publica Astro y su traductor en una carpeta limpia y compila AstroSetup.exe.
#   powershell -ExecutionPolicy Bypass -File tools/instalador/armar.ps1
# Necesita el SDK de .NET 8 y Inno Setup 6 (winget install JRSoftware.InnoSetup). El resultado queda en
# tools\instalador\salida\AstroSetup.exe (esa carpeta no va al repositorio).
# Sólo para quien desarrolla: la persona que usa Astro baja AstroSetup.exe y le hace doble clic.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $PSScriptRoot 'salida'
$app = Join-Path $out 'app'

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Falta Inno Setup 6. Instalalo con: winget install JRSoftware.InnoSetup' }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"   # SDK instalado sólo para el usuario
    if (-not (Test-Path $dotnet)) { throw 'Falta el SDK de .NET 8.' }
    $env:DOTNET_ROOT = Split-Path $dotnet
}

[xml]$project = Get-Content (Join-Path $root 'src\Navegador\Navegador.csproj')
$version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
& $dotnet publish (Join-Path $root 'src\Navegador') -c Release -o $app --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar Astro.' }
if (-not (Test-Path (Join-Path $app 'voz\astro-voz.exe'))) { throw 'Falta el traductor (voz\astro-voz.exe) en lo publicado.' }

& $iscc /Qp "/DAppVersion=$version" "/DOrigen=$app" (Join-Path $PSScriptRoot 'astro.iss')
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el instalador.' }

$setup = Get-Item (Join-Path $out 'AstroSetup.exe')
$files = Get-ChildItem $app -Recurse -File | Where-Object { $_.Extension -notin '.pdb', '.xml' }
"Astro $version"
"  instalado ocupa   {0:0.0} MB en {1} archivos" -f (($files | Measure-Object Length -Sum).Sum / 1MB), $files.Count
"  AstroSetup.exe    {0:0.0} MB · {1}" -f ($setup.Length / 1MB), $setup.FullName
