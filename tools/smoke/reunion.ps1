# Prueba oculta del modo reunión (fase 6.1): micrófono virtual de punta a punta, sin tocar el micrófono real.
#   powershell -ExecutionPolicy Bypass -File tools/smoke/reunion.ps1 [-Exe dist\Navegador.exe] [-Keep]
# Corre con --automation (ventana fuera de pantalla, sin foco) y una carpeta de datos temporal cuyo settings.json agrega
# --use-fake-device-for-media-stream (micrófono falso de Chromium, que hace "bip") y
# --autoplay-policy=no-user-gesture-required (en una reunión real el clic en "Unirse" habilita el audio).
# tools/smoke/reunion.html hace de sitio de reuniones: pide el micrófono y mide lo que recibe = lo que oiría la otra persona.
# Fases (el traductor de prueba devuelve un tono de 440 Hz por cada pedazo de voz que recibe):
#   sin-traductor  - va el micrófono falso: casi nada de 440 Hz.
#   traductor      - automático con traductor: sólo el tono (440 Hz domina) y la voz llega al navegador (mic_chunks > 0).
#   original       - "sólo tu voz original" con traductor conectado: vuelve el micrófono falso.
#   ambas          - tono + original de fondo: 440 Hz domina pero no es todo.
#   se-desconecta  - "sólo la traducida" y el traductor se va: la reunión no queda muda, vuelve el original.
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\dist\Navegador.exe'), [switch]$Keep)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$page = ([Uri](Resolve-Path (Join-Path $PSScriptRoot 'reunion.html')).Path).AbsoluteUri
$work = Join-Path $env:TEMP ("nav-reunion-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$settings = @{ BrowserArguments = '--process-per-site --use-fake-device-for-media-stream --autoplay-policy=no-user-gesture-required' }
$settings | ConvertTo-Json | Set-Content (Join-Path $work 'settings.json') -Encoding UTF8

function Measure-Step([string]$name) {
    "js window.reset(); 1`nwait 3000`njs JSON.stringify({fase: '$name', m: window.medicion})`n"
}
$steps = "go $page`nwait 2000`nmeeting on`nwait 3000`n" +
    "msgprobe`njs chrome.webview.postMessage('sonda-encendido'); 1`nwait 500`n" +
    (Measure-Step 'sin-traductor') +
    "translator tone`nwait 800`n" + (Measure-Step 'traductor') +
    "meetingstate`nwait 300`n" +
    "meetingvoice original`nwait 500`n" + (Measure-Step 'original') +
    "meetingvoice both`nwait 800`n" + (Measure-Step 'ambas') +
    "meetingvoice translated`ntranslator off`nwait 800`n" + (Measure-Step 'se-desconecta') +
    "shot reunion`nmeeting off`nwait 2500`n" +
    "js try { chrome.webview.postMessage('sonda-apagado'); 'enviado' } catch (e) { 'rechazado' }`nwait 500`n" +
    "newtab $page`nwait 2500`n" +
    "msgprobe`njs try { chrome.webview.postMessage('sonda-otra'); 'enviado' } catch (e) { 'rechazado' }`nwait 500`nclose`n"
$stepsFile = Join-Path $work 'reunion.txt'
Set-Content -Path $stepsFile -Value $steps -Encoding UTF8

Start-Process -FilePath $Exe -ArgumentList @('--data-dir', "`"$work`"", '--automation', "`"$stepsFile`"") -Wait
$log = Get-ChildItem (Join-Path $work 'logs') -Filter 'session-*.jsonl' | Select-Object -First 1
$events = Get-Content $log.FullName | ForEach-Object { $_ | ConvertFrom-Json }

$results = @{}
foreach ($e in $events | Where-Object { $_.name -eq 'automation_js' }) {
    try { $r = ($e.result | ConvertFrom-Json) | ConvertFrom-Json } catch { continue }
    if ($r.fase) { $results[$r.fase] = $r.m }
}
$state = $events | Where-Object { $_.name -eq 'meeting_state' } | Select-Object -First 1
$errors = @($events | Where-Object { $_.name -in 'automation_error', 'meeting_error' })
function Get-Probe([string]$s) { @($events | Where-Object { $_.name -eq 'probe_message' -and $_.message -match $s }).Count }

$checks = [ordered]@{
    'sin-traductor: va el micrófono'          = { $m = $results['sin-traductor']; $m -and $m.fraccion440 -lt 0.3 -and $m.nivelMax -gt 0.01 }
    'traductor: se oye el tono (440 Hz)'      = { $m = $results['traductor']; $m -and $m.fraccion440 -gt 0.8 -and [math]::Abs($m.picoHz - 440) -le 10 }
    'traductor: la voz llegó al navegador'    = { $state -and $state.mic_chunks -gt 0 -and $state.play_chunks -gt 0 }
    'original: vuelve el micrófono'           = { $m = $results['original']; $m -and $m.fraccion440 -lt 0.3 -and $m.nivelMax -gt 0.01 }
    # "Ambas" deja la voz original al 35 %: su energía fuera del tono tiene que ser ~12 % (0,35²) de la de "original".
    'ambas: tono con original de fondo'       = { $m = $results['ambas']; $o = $results['original']; $m -and $o -and $o.resto -gt 0 -and
                                                  $m.fraccion440 -gt 0.3 -and ($m.resto / $o.resto) -gt 0.04 -and ($m.resto / $o.resto) -lt 0.4 }
    # La voz original filtrada daría ~1; un clic aislado (corte antes de que el colchón se adapte) da ~0,05.
    'traductor: sólo el tono, sin la voz'     = { $m = $results['traductor']; $o = $results['original']; $m -and $o -and ($m.resto / $o.resto) -lt 0.1 }
    'se-desconecta: no queda muda'            = { $m = $results['se-desconecta']; $m -and $m.fraccion440 -lt 0.3 -and $m.nivelMax -gt 0.01 }
    'encendido: la reunión tiene canal'       = { (Get-Probe 'sonda-encendido') -gt 0 }
    'apagado: la página ya no tiene canal'    = { (Get-Probe 'sonda-apagado') -eq 0 }
    'otra pestaña: nunca tiene canal'         = { (Get-Probe 'sonda-otra') -eq 0 }
    'sin errores de la prueba'                = { $errors.Count -eq 0 }
}
$fail = $false
foreach ($k in $checks.Keys) {
    $ok = [bool](& $checks[$k])
    if (-not $ok) { $fail = $true }
    "{0,-42} {1}" -f $k, ($(if ($ok) { 'OK' } else { 'FALLA' }))
}
''
foreach ($f in 'sin-traductor', 'traductor', 'original', 'ambas', 'se-desconecta') { "{0,-14} {1}" -f $f, ($results[$f] | ConvertTo-Json -Compress) }
if ($results['original'] -and $results['original'].resto -gt 0) {
    "voz de fondo   ambas/original = {0:0.000} (esperado ~0,12) · traductor/original = {1:0.000} (esperado ~0; un clic suma ~0,05)" -f `
        ($results['ambas'].resto / $results['original'].resto), ($results['traductor'].resto / $results['original'].resto)
}
if ($state) { "estado         mic_chunks=$($state.mic_chunks) play_chunks=$($state.play_chunks) $($state.state)" }
foreach ($e in $errors) { "error          $($e | ConvertTo-Json -Compress)" }
"captura        $(Join-Path $work 'shots\reunion.png')"
if (-not $Keep -and -not $fail) { Start-Sleep 3; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
else { "carpeta        $work" }
''
if ($fail) { 'RESULTADO: FALLA'; exit 1 } else { 'RESULTADO: OK' }
