# Comprueba si RivaTuner (RTSS) sigue tirando el motor del navegador: abre el navegador N veces, oculto (fuera de
# la pantalla y sin tomar el foco, vía --automation), cada vez durante S segundos, y cuenta las caídas del proceso
# principal de WebView2.
# Ojo: RTSSHooks64.dll aparece cargada en todo programa con ventana (hook global de Windows) aunque el perfil
# tenga EnableHooking=0; lo que importa es si se cae. Antes de excluirlo: caídas en ~4 de cada 10 corridas.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools/rtss-check.ps1 [-Runs 8] [-Seconds 20]
param([int]$Runs = 8, [int]$Seconds = 20)
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\Navegador.exe'
$rtss = Get-Process RTSS -ErrorAction SilentlyContinue
"RTSS: " + $(if ($rtss) { "corriendo desde $($rtss.StartTime)" } else { 'no está corriendo' })
$steps = Join-Path $env:TEMP 'navegador-rtss-check-steps.txt'
"go https://www.infobae.com`r`nwait $($Seconds * 1000)`r`nclose" | Set-Content $steps -Encoding UTF8
$total = 0
for ($i = 1; $i -le $Runs; $i++) {
    $data = Join-Path $env:TEMP "navegador-rtss-check-$i"
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
    $p = Start-Process $exe -ArgumentList "--data-dir `"$data`" --automation `"$steps`"" -PassThru
    if (-not $p.WaitForExit(($Seconds + 30) * 1000)) { Stop-Process $p -Force }
    $log = Get-ChildItem "$data\logs\*.jsonl" | Select-Object -First 1
    $n = (Select-String -Path $log -Pattern 'BrowserProcessExited').Count
    $total += $n
    "Corrida ${i}: $n caídas"
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}
Remove-Item $steps -ErrorAction SilentlyContinue
"Total: $total caídas en $Runs corridas"
