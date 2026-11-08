# Comprueba si RivaTuner (RTSS) sigue tirando el motor del navegador: abre el navegador N veces con una carpeta
# de datos aparte, cada vez durante S segundos, y cuenta las caídas del proceso principal de WebView2.
# Ojo: RTSSHooks64.dll aparece cargada en todo programa con ventana (hook global de Windows) aunque el perfil
# tenga EnableHooking=0; lo que importa es si se cae. Antes de excluirlo: caídas en ~4 de cada 10 corridas.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools/rtss-check.ps1 [-Runs 8] [-Seconds 20]
param([int]$Runs = 8, [int]$Seconds = 20)
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\Navegador.exe'
$rtss = Get-Process RTSS -ErrorAction SilentlyContinue
"RTSS: " + $(if ($rtss) { "corriendo desde $($rtss.StartTime)" } else { 'no está corriendo' })
$total = 0
for ($i = 1; $i -le $Runs; $i++) {
    $data = Join-Path $env:TEMP "navegador-rtss-check-$i"
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
    $p = Start-Process $exe -ArgumentList "--data-dir `"$data`" https://www.infobae.com" -PassThru
    Start-Sleep $Seconds
    $p.CloseMainWindow() | Out-Null; $p.WaitForExit(10000) | Out-Null
    if (-not $p.HasExited) { Stop-Process $p -Force }
    $log = Get-ChildItem "$data\logs\*.jsonl" | Select-Object -First 1
    $n = (Select-String -Path $log -Pattern 'BrowserProcessExited').Count
    $total += $n
    "Corrida ${i}: $n caídas"
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}
"Total: $total caídas en $Runs corridas"
