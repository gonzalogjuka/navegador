# RivaTuner Statistics Server (overlay de MSI Afterburner) inyecta RTSSHooks64.dll en los procesos que dibujan con
# DirectX para contar FPS y dibujar el overlay. En el proceso principal de WebView2 esa DLL provoca ACCESS_VIOLATION
# (visto en los minidumps de crashpad) y el motor del navegador se cae. Este script crea perfiles de RTSS que
# desactivan la inyección en msedgewebview2.exe (motor) y Navegador.exe (ventana). El overlay en juegos sigue igual.
#
# Uso (PowerShell como administrador):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/rtss-exclude.ps1
# Después cerrá MSI Afterburner y RivaTuner desde el área de notificación y volvé a abrirlos.
# Para deshacer: borrar msedgewebview2.exe.cfg y Navegador.exe.cfg de la carpeta Profiles de RTSS.
$dir = Join-Path ${env:ProgramFiles(x86)} 'RivaTuner Statistics Server\Profiles'
if (-not (Test-Path $dir)) { "RTSS no está instalado; no hay nada que hacer."; exit 0 }
foreach ($exe in 'msedgewebview2.exe', 'Navegador.exe') {
    $profile = Join-Path $dir "$exe.cfg"
    "[Hooking]`r`nEnableHooking=0`r`n" | Set-Content -Path $profile -Encoding ASCII -NoNewline -ErrorAction Stop
    "Perfil creado: $profile"
}
