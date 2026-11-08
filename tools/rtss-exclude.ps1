# RivaTuner Statistics Server (overlay de MSI Afterburner) inyecta RTSSHooks64.dll en los procesos que dibujan
# con la GPU y tira abajo el proceso principal de WebView2 (ACCESS_VIOLATION en RTSSHooks64.dll, visto en los
# minidumps de crashpad). Este script crea un perfil de RTSS que desactiva la inyección en msedgewebview2.exe.
# Necesita permisos de administrador (la carpeta está en Program Files). Después reiniciá RTSS / Afterburner.
#
# Uso (PowerShell como administrador):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/rtss-exclude.ps1
# Para deshacer: borrar el archivo msedgewebview2.exe.cfg de la carpeta Profiles de RTSS.
$dir = Join-Path ${env:ProgramFiles(x86)} 'RivaTuner Statistics Server\Profiles'
if (-not (Test-Path $dir)) { "RTSS no está instalado; no hay nada que hacer."; exit 0 }
$profile = Join-Path $dir 'msedgewebview2.exe.cfg'
"[Hooking]`r`nEnableHooking=0`r`n" | Set-Content -Path $profile -Encoding ASCII -NoNewline -ErrorAction Stop
"Perfil creado: $profile"
