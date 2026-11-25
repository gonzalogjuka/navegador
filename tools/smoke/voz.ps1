# Prueba oculta del traductor de voz de Astro, de punta a punta (fase 8): una grabación en español hace de micrófono,
# una página hace de reunión, Astro prende solo su traductor (voz\astro-voz.exe) y se mide lo que llega a la reunión.
#   powershell -ExecutionPolicy Bypass -File tools/smoke/voz.ps1 [-Seconds 40] [-Url https://meet.jit.si/sala] [-Backend auto|cpu|vulkan]
#                                                               [-Carga] [-Limpio] [-Exe dist\Navegador.exe] [-Wav archivo.wav]
# Corre con --automation (ventana fuera de pantalla, sin foco) y el micrófono falso de Chromium leyendo el WAV
# (--use-file-for-fake-audio-capture), que se repite solo. No usa tu micrófono ni suena por los parlantes.
#   sin -Url   la reunión es tools/smoke/reunion.html, con el modo reunión prendido a mano.
#   -Url       una reunión real (p. ej. https://meet.jit.si/una-sala): el modo reunión se tiene que prender solo.
#   -Carga     fase 8.2: durante la reunión abre pestañas pesadas (Maps, YouTube, diarios); la demora no tiene que subir.
#   -Limpio    borra los modelos antes: prueba la descarga de la primera vez (58 MB).
# Qué mide (todo sale del log de Astro): frases detectadas y dichas, demora de tu última palabra a la voz inglesa,
# lo que tarda el reconocimiento, y la RAM del traductor y del resto de Astro durante la reunión.
# Los datos quedan en %TEMP%\astro-voz-prueba (los modelos se bajan una sola vez). Sale con 1 si algo falla.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\dist\Navegador.exe'),
    [string]$Wav = (Join-Path $PSScriptRoot 'muestras\es-atmolisis.wav'),
    [int]$Seconds = 40,
    [string]$Url = '',
    [ValidateSet('auto', 'cpu', 'vulkan')][string]$Backend = 'auto',
    [switch]$Carga,
    [switch]$Limpio,
    [switch]$Volcar,             # deja cada frase (WAV + texto en inglés) en %TEMP%\astro-voz-prueba\frases, para afinar
    [int]$MaxMedianaMs = 1300,   # la fase 7 (motor web): 1,3 s
    [int]$MaxVozMb = 400,        # meta de la fase 8: 200–400 MB
    [int]$MinFrases = 4
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$Wav = (Resolve-Path $Wav).Path
$data = Join-Path $env:TEMP 'astro-voz-prueba'
New-Item -ItemType Directory $data -Force | Out-Null
foreach ($old in 'logs', 'shots', 'snapshots', 'session.json', 'data.json') {
    Remove-Item (Join-Path $data $old) -Recurse -Force -ErrorAction SilentlyContinue
}
if ($Limpio) { Remove-Item (Join-Path $data 'modelos') -Recurse -Force -ErrorAction SilentlyContinue }

$browserArgs = '--process-per-site --use-fake-device-for-media-stream --autoplay-policy=no-user-gesture-required ' +
    "--use-file-for-fake-audio-capture=$($Wav -replace '\\', '/')"
@{ ApiEnabled = $false; VozBackend = $Backend; BrowserArguments = $browserArgs } | ConvertTo-Json |
    Set-Content (Join-Path $data 'settings.json') -Encoding UTF8

$page = ([Uri](Resolve-Path (Join-Path $PSScriptRoot 'reunion.html')).Path).AbsoluteUri
$steps = New-Object System.Collections.Generic.List[string]
if ($Url) { $steps.Add("go $Url") } else { $steps.Add("go $page"); $steps.Add('wait 1500'); $steps.Add('meeting on') }
$steps.Add('waittranslator on 900')   # la primera vez baja los modelos
if ($Carga) {
    # Pestañas pesadas abiertas una atrás de otra mientras "hablás"; al final se vuelve a la reunión.
    $heavy = 'https://www.google.com/maps/place/Buenos+Aires', 'https://www.youtube.com/', 'https://www.lanacion.com.ar/',
        'https://www.infobae.com/', 'https://www.mercadolibre.com.ar/', 'https://es.wikipedia.org/wiki/Argentina'
    $each = [math]::Max(4, [math]::Floor(($Seconds - 8) / $heavy.Count))
    foreach ($h in $heavy) { $steps.Add("newtab $h"); $steps.Add("wait $($each * 1000)") }
    $steps.Add('tab 0')
    $steps.Add("wait $([math]::Max(1, $Seconds - $each * $heavy.Count) * 1000)")
}
else {
    $steps.Add("wait $($Seconds * 1000)")
}
$steps.Add('meetingstate')
$steps.Add('wait 400')
if (-not $Url) { $steps.Add('js JSON.stringify({oye: window.medicion})') }
$steps.Add('shot voz')
$steps.Add('close')
$stepsFile = Join-Path $data 'pasos.txt'
Set-Content -Path $stepsFile -Value $steps -Encoding UTF8

$dump = Join-Path $data 'frases'
Remove-Item $dump -Recurse -Force -ErrorAction SilentlyContinue
$env:ASTRO_VOZ_VOLCAR = if ($Volcar) { $dump } else { $null }
$t0 = Get-Date
Start-Process -FilePath $Exe -ArgumentList @('--data-dir', "`"$data`"", '--automation', "`"$stepsFile`"") -Wait
$took = ((Get-Date) - $t0).TotalSeconds
Start-Sleep -Milliseconds 500
$left = @(Get-Process astro-voz -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith((Split-Path $Exe), 'OrdinalIgnoreCase') })

$log = Get-ChildItem (Join-Path $data 'logs') -Filter 'session-*.jsonl' | Select-Object -First 1
$events = Get-Content $log.FullName -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json }
function Get-Events([string[]]$names) { @($events | Where-Object { $_.name -in $names }) }
function Get-Median($values) { $s = @($values | Sort-Object); if ($s.Count) { $s[[int][math]::Floor($s.Count / 2)] } else { $null } }

$ready = Get-Events 'voz_ready' | Select-Object -First 1
$phrases = @(Get-Events 'voz_phrase')   # @(): con un solo evento PowerShell devuelve el objeto suelto, sin .Count
$spoken = @($phrases | Where-Object { $_.spoken })
$merged = @($phrases | Where-Object { $_.merged -gt 1 })
$errors = @(Get-Events 'voz_error', 'automation_error', 'meeting_error')
$exits = @(Get-Events 'voz_exit')
$stats = Get-Events 'voz_stats' | Select-Object -Last 1   # contadores del traductor (cada ~5 s de voz)
$state = Get-Events 'meeting_state' | Select-Object -First 1
$page2 = if ($state) { $state.state | ConvertFrom-Json } else { $null }
$hears = $null
foreach ($e in Get-Events 'automation_js') {
    try { $r = ($e.result | ConvertFrom-Json) | ConvertFrom-Json } catch { continue }
    if ($r.oye) { $hears = $r.oye }
}
# RAM: sólo las muestras con el traductor listo (durante la reunión traducida). Del traductor, voz_mb es la memoria
# física que usa (la que Astro cuenta para el tope); voz_private_mb suma además lo que Whisper reserva sin usar durante
# los ~0,3 s de cada frase (~650 MB), así que su máximo depende de si la muestra cayó justo en una frase.
$samples = @($events | Where-Object { $_.type -eq 'sample' -and $ready -and $_.ts -ge $ready.ts -and $null -ne $_.voz_mb })
$vozMb = @($samples | ForEach-Object { [double]$_.voz_mb })
$vozPrivMb = @($samples | ForEach-Object { [double]$_.voz_private_mb })
$restMb = @($samples | ForEach-Object { [double]$_.app_private_mb - [double]$_.voz_mb })
$totalMb = @($samples | ForEach-Object { [double]$_.app_private_mb })
$median = Get-Median $spoken.total_ms
$worst = ($spoken.total_ms | Measure-Object -Maximum).Maximum
$vozMax = ($vozMb | Measure-Object -Maximum).Maximum

$checks = [ordered]@{
    'el traductor quedó listo'                    = { [bool]$ready }
    'la voz tiene prioridad (proceso y MMCSS)'    = { $ready -and $ready.high_priority -and $ready.mmcss }
    'la voz llegó al traductor y oyó voz'         = { $stats -and $stats.mic_frames -gt 0 -and $stats.voiced -gt 0 }
    "frases dichas en inglés (>= $MinFrases)"     = { $spoken.Count -ge $MinFrases }
    "demora mediana <= $MaxMedianaMs ms"          = { $null -ne $median -and $median -le $MaxMedianaMs }
    "RAM del traductor <= $MaxVozMb MB"           = { $vozMb.Count -gt 0 -and $vozMax -le $MaxVozMb }
    # Se cuenta contra las frases dichas hasta el momento en que se le preguntó el estado a la reunión.
    'la voz inglesa llegó a la reunión'           = { $state -and $state.play_chunks -gt 0 -and
                                                      $state.play_chunks -ge @($spoken | Where-Object { $_.ts -lt $state.ts - 0.3 }).Count }
    'no se descartó voz (cola de la reunión)'     = { $page2 -and $page2.dropped -eq 0 }
    'ninguna frase atrasada (sin juntar frases)'  = { $merged.Count -eq 0 }
    'sin errores'                                 = { $errors.Count -eq 0 }
    'el traductor se cerró con Astro'             = { $left.Count -eq 0 }
}
if (-not $Url) { $checks['la reunión oye voz (no silencio)'] = { $hears -and $hears.nivelMax -gt 0.01 } }
if ($Url) { $checks['el modo reunión se prendió solo'] = { @(Get-Events 'meeting_auto').Count -gt 0 } }

$fail = $false
foreach ($k in $checks.Keys) {
    $ok = [bool](& $checks[$k])
    if (-not $ok) { $fail = $true }
    "{0,-46} {1}" -f $k, ($(if ($ok) { 'OK' } else { 'FALLA' }))
}
''
"corrida        {0:0} s · reunión {1} · {2} s traduciendo{3}" -f $took, $(if ($Url) { $Url } else { 'tools/smoke/reunion.html' }), $Seconds, $(if ($Carga) { ' · con pestañas pesadas' } else { '' })
if ($ready) {
    "traductor      $($ready.backend), $($ready.threads) hilos, $($ready.asr), voz «$($ready.voice)», listo en $($ready.load_ms) ms" +
        " · prioridad alta: $(if ($ready.high_priority) { 'sí' } else { 'no' }) · MMCSS: $(if ($ready.mmcss) { 'sí' } else { 'no' })"
}
"frases         detectadas $($phrases.Count) · dichas $($spoken.Count) · sin nada que decir $($phrases.Count - $spoken.Count) · juntadas por atraso $($merged.Count)"
if ($spoken.Count) {
    "demora         de tu última palabra a la voz inglesa: mediana {0} ms, peor {1} ms (fase 7: mediana 1300–1374 ms)" -f $median, $worst
    "reconocimiento mediana {0} ms, peor {1} ms · voz inglesa mediana {2} ms" -f (Get-Median $spoken.asr_ms), ($spoken.asr_ms | Measure-Object -Maximum).Maximum, (Get-Median $spoken.tts_ms)
}
if ($vozMb.Count) {
    "RAM            traductor: mediana {0:0} MB, máx. {1:0} MB · resto de Astro: mediana {2:0} MB · total: mediana {3:0} MB, máx. {4:0} MB ({5} muestras; fase 7: ~2700–2900 MB en total)" -f `
        (Get-Median $vozMb), $vozMax, (Get-Median $restMb), (Get-Median $totalMb), ($totalMb | Measure-Object -Maximum).Maximum, $samples.Count
    "               traductor, memoria privada (con las reservas de un instante de Whisper): mediana {0:0} MB, máx. {1:0} MB" -f `
        (Get-Median $vozPrivMb), ($vozPrivMb | Measure-Object -Maximum).Maximum
}
if ($stats) { "detector       pedazos de voz recibidos $($stats.mic_frames) · ventanas de 32 ms $($stats.windows), con voz $($stats.voiced)" }
if ($state) { "reunión        pedazos de voz al traductor $($state.mic_chunks) · frases de voz inglesa a la reunión $($state.play_chunks) · $($state.state)" }
if ($hears) { "lo que oye     $($hears | ConvertTo-Json -Compress)" }
if ($Carga) {
    $discards = @(Get-Events 'budget_discard')
    $last = $events | Where-Object { $_.type -eq 'sample' } | Select-Object -Last 1
    "carga          pestañas $($last.tabs) (vivas $($last.tabs_live), suspendidas $($last.tabs_suspended), descartadas $($last.tabs_discarded)) · descartes por tope $($discards.Count)"
    foreach ($s in $samples) {
        "  muestra      total {0,5:0} MB = traductor {1,4:0} + páginas y GPU {2,5:0} + resto {3,4:0} · CPU {4,5:0.0} % · vivas {5}" -f `
            [double]$s.app_private_mb, [double]$s.voz_mb, ([double]$s.renderer_private_mb + [double]$s.gpu_private_mb),
            ([double]$s.app_private_mb - [double]$s.voz_mb - [double]$s.renderer_private_mb - [double]$s.gpu_private_mb), [double]$s.cpu_pct, $s.tabs_live
    }
}
foreach ($e in $exits) { "traductor      terminó: código $($e.exit_code), pedido=$($e.stopping)" }
foreach ($e in $errors) { "error          $($e | ConvertTo-Json -Compress)" }
foreach ($p in $left) { "error          quedó un astro-voz.exe abierto (PID $($p.Id))" }
"log            $($log.FullName)"
"captura        $(Join-Path $data 'shots\voz.png')"
''
if ($fail) { 'RESULTADO: FALLA'; exit 1 } else { 'RESULTADO: OK' }
