# Prueba del instalador de Astro (fase 8.3): instala AstroSetup.exe en una carpeta de prueba, sin ventanas, comprueba lo
# que dejó, corre desde ahí la prueba oculta del traductor y desinstala comprobando que no queden restos.
#   powershell -ExecutionPolicy Bypass -File tools/instalador/probar.ps1 [-SinVoz] [-ProbarDescargaDotNet]
# Instala para tu usuario pero en %LOCALAPPDATA%\Temp\astro-instalacion-prueba (no en la carpeta real) y lo saca al terminar. Durante
# la prueba aparecen y desaparecen los accesos directos "Astro" (menú Inicio y escritorio) y el registro como navegador.
# No toca tus datos (%LOCALAPPDATA%\Navegador). Si ya hay un Astro instalado de verdad, no hace nada: lo pisaría.
#   -ProbarDescargaDotNet   hace de cuenta que falta .NET 8: el instalador lo baja de Microsoft (~57 MB) y NO lo instala.
#   -SinVoz                 no corre tools/smoke/voz.ps1 con la copia instalada.
param([switch]$SinVoz, [switch]$ProbarDescargaDotNet)
$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'salida\AstroSetup.exe'
if (-not (Test-Path $setup)) { throw "Falta $setup. Armalo con tools/instalador/armar.ps1" }
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{F8223511-D563-45D1-9A00-BEC61BE78DEC}_is1'
$browserKey = 'HKCU:\Software\Clients\StartMenuInternet\Navegador'
if (Test-Path $uninstallKey) { throw 'Ya hay un Astro instalado para este usuario: la prueba lo pisaría. Desinstalalo antes o probá en otro usuario.' }
$hadBrowser = Test-Path $browserKey

$work = Join-Path $env:LOCALAPPDATA 'Temp\astro-instalacion-prueba'   # no $env:TEMP: puede venir en formato corto (GONZAL~1)
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null
$dir = Join-Path $work 'Astro'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Astro.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Astro.lnk'
$results = [ordered]@{}
function Check([string]$name, [bool]$ok) { $results[$name] = $ok }

# ---------- Instalar ----------
$installLog = Join-Path $work 'instalar.log'
$installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$dir`"", "/LOG=`"$installLog`"")
if ($ProbarDescargaDotNet) { $installArgs += '/DOTNET=probar' }
if ($hadBrowser) { $installArgs += '/MERGETASKS="!registrar"' }   # ya hay otra copia registrada como navegador: no se toca
$t0 = Get-Date
$p = Start-Process $setup -ArgumentList $installArgs -Wait -PassThru
$installSeconds = ((Get-Date) - $t0).TotalSeconds
$logText = if (Test-Path $installLog) { Get-Content $installLog -Raw } else { '' }

Check 'instaló sin error (código 0)' ($p.ExitCode -eq 0)
Check 'Navegador.exe y el traductor están' ((Test-Path "$dir\Navegador.exe") -and (Test-Path "$dir\voz\astro-voz.exe") -and (Test-Path "$dir\voz\runtimes\win-x64\whisper.dll"))
Check 'sin archivos de desarrollo (.pdb, .xml)' (@(Get-ChildItem $dir -Recurse -Include *.pdb, *.xml -ErrorAction SilentlyContinue).Count -eq 0)
Check 'acceso directo en el menú Inicio' (Test-Path $startMenu)
Check 'acceso directo en el escritorio' (Test-Path $desktop)
$un = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
Check 'figura como "Astro" para desinstalar' ($un -and $un.DisplayName -eq 'Astro' -and (Test-Path ($un.UninstallString -replace '"', '')))
if (-not $hadBrowser) {
    $cmd = (Get-ItemProperty "$browserKey\shell\open\command" -ErrorAction SilentlyContinue).'(default)'
    Check 'Windows lo ofrece como navegador "Astro"' ($cmd -and $cmd.Contains($dir) -and
        (Get-ItemProperty "$browserKey\Capabilities" -ErrorAction SilentlyContinue).ApplicationName -eq 'Astro')
}
if ($ProbarDescargaDotNet) { Check '.NET 8: lo bajó de Microsoft (sin instalarlo)' ($logText -match 'bajado \((\d{8,}) bytes\)') }
else { Check '.NET 8: lo detectó instalado' ($logText -match 'ya est') }
$link = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenu)
Check 'el acceso directo abre el Astro instalado' ($link.TargetPath -eq "$dir\Navegador.exe")
$installedMb = (Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB

# ---------- Usar lo instalado ----------
$vozOut = @()
if (-not $SinVoz) {
    $vozOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '..\smoke\voz.ps1') -Exe "$dir\Navegador.exe" -Seconds 25 -MinFrases 3
    Check 'la copia instalada traduce (tools/smoke/voz.ps1)' ($LASTEXITCODE -eq 0)
}

# ---------- Desinstalar ----------
$uninstaller = if ($un) { $un.UninstallString -replace '"', '' } else { "$dir\unins000.exe" }
$up = Start-Process $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
for ($i = 0; $i -lt 50 -and (Test-Path $dir); $i++) { Start-Sleep -Milliseconds 200 }   # el desinstalador se borra a sí mismo al final
Check 'desinstaló sin error (código 0)' ($up.ExitCode -eq 0)
Check 'no quedó la carpeta del programa' (-not (Test-Path $dir))
Check 'no quedaron accesos directos' (-not (Test-Path $startMenu) -and -not (Test-Path $desktop))
Check 'no quedó en la lista de programas' (-not (Test-Path $uninstallKey))
if (-not $hadBrowser) {
    Check 'no quedó registrado como navegador' (-not (Test-Path $browserKey) -and -not (Test-Path 'HKCU:\Software\Classes\NavegadorURL') -and
        -not (Test-Path 'HKCU:\Software\Classes\NavegadorHTML') -and
        $null -eq (Get-ItemProperty 'HKCU:\Software\RegisteredApplications' -ErrorAction SilentlyContinue).Navegador)
}

$fail = $false
foreach ($k in $results.Keys) {
    if (-not $results[$k]) { $fail = $true }
    "{0,-50} {1}" -f $k, ($(if ($results[$k]) { 'OK' } else { 'FALLA' }))
}
''
"instalador     {0:0.0} MB · instaló en {1:0} s · instalado ocupa {2:0.0} MB" -f ((Get-Item $setup).Length / 1MB), $installSeconds, $installedMb
foreach ($line in ($logText -split "`r?`n" | Where-Object { $_ -match '\.NET 8' })) { "registro       $($line -replace '^\S+\s+\S+\s+', '')" }
foreach ($line in ($vozOut | Where-Object { $_ -match '^(demora|RAM  |frases|RESULTADO)' -or $_ -match 'FALLA$' })) { "voz            $line" }
if ($fail) { "carpeta        $work"; ''; 'RESULTADO: FALLA'; exit 1 }
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
''
'RESULTADO: OK'
