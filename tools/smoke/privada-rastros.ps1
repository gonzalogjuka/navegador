# Verifica que la ventana privada no deja rastros en disco. Corre oculto (--automation) con carpetas de datos temporales.
#   powershell -ExecutionPolicy Bypass -File tools/smoke/privada-rastros.ps1 [-Exe dist\Navegador.exe]
# Cuatro corridas que visitan las mismas páginas (es.wikipedia.org con una marca en la URL, example.org con otra, y
# guardan una cookie y un valor de localStorage con la marca):
#   control       - en la ventana NORMAL: la búsqueda TIENE que encontrar la marca (si no, la búsqueda no sirve).
#   privada       - ventana normal + ventana privada que navega: 0 archivos con la marca.
#   cierra-normal - abre la privada, cierra la normal y sigue navegando en la privada: 0 archivos con la marca y,
#                   desde que se cerró la normal, ningún evento en el log salvo los de la propia prueba.
#   solo          - arrancando con --private: 0 archivos con la marca, ningún evento salvo los de la prueba y sin data.json.
# Se busca en TODA la carpeta de datos (logs, data.json, session.json, snapshots, perfiles de WebView2) la marca y
# "wikimedia" (host de las imágenes de Wikipedia: no aparece en los pasos), en ASCII/UTF-8 y en UTF-16.
# Los pasos arman las URL por partes para que la marca no quede escrita en el log de la prueba.
# -Keep deja las carpetas temporales para revisarlas.
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\dist\Navegador.exe'), [switch]$Keep)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$work = Join-Path $env:TEMP ("nav-rastros-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null

$visit = @'
js location.href = "https://es." + "wiki" + "pedia.org/wiki/Faro?" + "zq" + "x7" + "pagina"; 1
wait 7000
js document.cookie = "c=" + "zq" + "x7" + "galleta; max-age=99999"; localStorage.setItem("k", "zq" + "x7" + "local"); 1
js location.hostname.endsWith("wiki" + "pedia.org") && document.cookie.indexOf("zq" + "x7") >= 0 && localStorage.getItem("k").length > 0
js location.href = "https://example" + ".org/?" + "zq" + "x7" + "otra"; 1
wait 4000
js location.hostname.length == 11 && location.search.indexOf("zq" + "x7") > 0
'@
$runs = [ordered]@{
    control = @{ Args = @(); Steps = "go https://example.com`nwait 3000`n$visit`nclose`n" }
    privada = @{ Args = @(); Steps = "go https://example.com`nwait 3000`nprivate`nwait 1500`n$visit`nclose`nmain`nwait 500`nclose`n" }
    'cierra-normal' = @{ Args = @(); Steps = "go https://example.com`nwait 3000`nprivate`nwait 1500`nmain`nclose`nwait 1000`ntoprivate`n$visit`nclose`n" }
    solo    = @{ Args = @('--private'); Steps = "wait 1500`n$visit`nclose`n" }
}
$needles = @('zqx7', 'wikimedia')

function Find-Needles([string]$dir) {
    $hits = @()
    foreach ($f in Get-ChildItem $dir -Recurse -File -Force) {
        try { $bytes = [IO.File]::ReadAllBytes($f.FullName) } catch { continue }
        $latin = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
        foreach ($n in $needles) {
            $utf16 = [Text.Encoding]::GetEncoding(28591).GetString([Text.Encoding]::Unicode.GetBytes($n))
            if ($latin.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $latin.IndexOf($utf16, [StringComparison]::Ordinal) -ge 0) {
                $hits += "$($f.FullName.Substring($dir.Length + 1)) [$n]"
            }
        }
    }
    return $hits
}

$fail = $false
foreach ($name in $runs.Keys) {
    $data = Join-Path $work $name
    $steps = Join-Path $work "$name.txt"
    Set-Content -Path $steps -Value $runs[$name].Steps -Encoding UTF8
    $p = Start-Process -FilePath $Exe -ArgumentList (@('--data-dir', "`"$data`"") + $runs[$name].Args + @('--automation', "`"$steps`"")) -PassThru -Wait
    Start-Sleep -Seconds 5   # los procesos de WebView2 terminan después de la app
    $hits = @(Find-Needles $data)
    $files = (Get-ChildItem $data -Recurse -File -Force).Count
    # Eventos que no son de la prueba en el tramo en que sólo había ventanas privadas ('-' = no aplica).
    $events = @()
    $trues = 0   # comprobaciones de los pasos que dieron true: navegó, guardó la cookie y localStorage (tienen que ser 2)
    $counting = $name -eq 'solo'
    foreach ($log in Get-ChildItem (Join-Path $data 'logs') -Filter *.jsonl -ErrorAction SilentlyContinue) {
        foreach ($e in Get-Content $log.FullName | ForEach-Object { $_ | ConvertFrom-Json }) {
            if ($e.name -eq 'automation_js' -and $e.result -eq 'true') { $trues++ }
            if ($name -eq 'cierra-normal' -and $e.name -eq 'shutdown' -and -not $e.private) { $counting = $true; continue }
            if ($counting -and $e.type -ne 'sample' -and $e.name -notlike 'automation_*' -and $e.name -notin 'record_start', 'record_stop') { $events += $e.name }
        }
    }
    $eventsShown = if ($name -in 'solo', 'cierra-normal') { $events.Count } else { '-' }
    $profiles = @(Get-ChildItem (Join-Path $data 'WebView2\EBWebView') -Directory -Filter 'WV2Profile_privada-*' -ErrorAction SilentlyContinue).Count
    $dataJson = Test-Path (Join-Path $data 'data.json')

    $ok = $trues -eq 2 -and $(switch ($name) {
        'control'       { $hits.Count -gt 0 }
        'privada'       { $hits.Count -eq 0 -and $profiles -eq 0 }
        'cierra-normal' { $hits.Count -eq 0 -and $profiles -eq 0 -and $counting -and $events.Count -eq 0 }
        'solo'          { $hits.Count -eq 0 -and $profiles -eq 0 -and $events.Count -eq 0 -and -not $dataJson }
    })
    if (-not $ok) { $fail = $true }
    Write-Output ("{0,-13} {1}  navego={2}/2  archivos={3}  con-rastro={4}  perfiles-privados={5}  data.json={6}  eventos-solo-privada={7}" -f `
        $name, ($(if ($ok) { 'OK   ' } else { 'FALLA' })), $trues, $files, $hits.Count, $profiles, $dataJson, $eventsShown)
    if ($name -eq 'control') { $hits | Select-Object -First 8 | ForEach-Object { Write-Output "                encontrado: $_" } }
    else { $hits | ForEach-Object { Write-Output "                RASTRO: $_" } }
    if ($events.Count -gt 0) { Write-Output ("                eventos: " + (($events | Group-Object | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' ')) }
}
if ($Keep) { Write-Output "Carpetas: $work" } else { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
if ($fail) { Write-Output 'RESULTADO: FALLA'; exit 1 } else { Write-Output 'RESULTADO: OK'; exit 0 }
