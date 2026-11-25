# Prueba oculta de la API local (fase 6.2) con el modo reunión: el traductor es una app aparte que se conecta por
# WebSocket (tools/integraciones/Eco, modo tono), como lo hará la app de traducción real.
#   powershell -ExecutionPolicy Bypass -File tools/smoke/integracion.ps1 [-Exe dist\Navegador.exe] [-Keep]
# Navegador oculto (--automation) con carpeta temporal, micrófono falso de Chromium y la API en un puerto libre al azar para
# no chocar con un navegador abierto. Recorrido (los pasos del navegador y las corridas de Eco van coordinados por tiempo):
#   ataques       - pedidos crudos al puerto con Origin (como haría una página web) o con un Host ajeno: 403.
#   eco 1         - primera vez: el navegador muestra el pedido de permiso (captura "permiso"), se aprueba, Eco guarda
#                   el token; en la reunión se oye el tono (api-tono) y se mide la ida y vuelta.
#   se fue        - Eco 1 termina: la reunión vuelve a la voz original (api-se-fue).
#   eco 2         - vuelve con el token: entra sin preguntar y se oye el tono (api-token).
#   olvidar       - "Olvidar apps conectadas": Eco 2 queda afuera y vuelve la voz original (olvidada).
#   eco 3         - con el token viejo: el navegador vuelve a preguntar, se rechaza y Eco 3 termina con "denied".
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\dist\Navegador.exe'), [switch]$Keep)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$page = ([Uri](Resolve-Path (Join-Path $PSScriptRoot 'reunion.html')).Path).AbsoluteUri
$work = Join-Path $env:TEMP ("nav-api-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$data = Join-Path $work 'datos'
New-Item -ItemType Directory $data | Out-Null
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start()
$port = $probe.LocalEndpoint.Port; $probe.Stop()   # puerto libre: no choca con otro navegador ni con otra corrida
@{ BrowserArguments = '--process-per-site --use-fake-device-for-media-stream --autoplay-policy=no-user-gesture-required'; ApiPort = $port; VozEnabled = $false } |
    ConvertTo-Json | Set-Content (Join-Path $data 'settings.json') -Encoding UTF8

# Eco compilado una vez (arranca en ~0,1 s, no como dotnet run).
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
& $dotnet build (Join-Path $repo 'tools\integraciones\Eco') -c Release -o (Join-Path $work 'eco') --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'No compila tools\integraciones\Eco (correr dotnet build ahí para ver el error)' }
$eco =Join-Path $work 'eco\Eco.exe'
$tokenFile = Join-Path $work 'eco-token.txt'
function Start-Eco([int]$n, [int]$seconds) {
    $p = Start-Process -FilePath $eco -ArgumentList @('--tone', '--data-dir', "`"$data`"", '--token-file', "`"$tokenFile`"", '--seconds', $seconds) `
        -RedirectStandardOutput (Join-Path $work "eco$n.txt") -WindowStyle Hidden -PassThru
    $null = $p.Handle   # sin esto PowerShell no guarda el código de salida
    return $p
}

function Measure-Step([string]$name) {
    "js window.reset(); 1`nwait 3000`njs JSON.stringify({fase: '$name', m: window.medicion})`n"
}
# Los pasos esperan lo que hace Eco (waitapproval, waittranslator), no tiempos fijos.
$steps = "go $page`nwait 2000`nmeeting on`nwait 3000`n" +
    "waitapproval`nshot permiso`napiapprove`nwaittranslator on`nwait 1000`n" +  # Eco 1 aprobado
    (Measure-Step 'api-tono') + "meetingstate`nwait 300`n" +
    "waittranslator off`nwait 300`n" + (Measure-Step 'api-se-fue') +           # Eco 1 se fue
    "waittranslator on`nwait 800`n" + (Measure-Step 'api-token') +             # Eco 2 entra con su token
    "forgetapps`nwaittranslator off`nwait 300`n" + (Measure-Step 'olvidada') +
    "waitapproval`nshot permiso-de-nuevo`napideny`nwait 1500`nclose`n"          # Eco 3 rechazado
$stepsFile = Join-Path $work 'pasos.txt'
Set-Content -Path $stepsFile -Value $steps -Encoding UTF8

$browser = Start-Process -FilePath $Exe -ArgumentList @('--data-dir', "`"$data`"", '--automation', "`"$stepsFile`"") -PassThru
for ($i = 0; $i -lt 100 -and -not (Test-Path (Join-Path $data 'api.json')); $i++) { Start-Sleep -Milliseconds 100 }

# Ataques: una página web (con Origin) y un Host ajeno (DNS rebinding) tienen que recibir 403.
function Send-Raw([string]$extra, [string]$hostHeader = "localhost:$port") {
    $c = [Net.Sockets.TcpClient]::new('127.0.0.1', $port)
    $s = $c.GetStream()
    $req = "GET /v1 HTTP/1.1`r`nHost: $hostHeader`r`nUpgrade: websocket`r`nConnection: Upgrade`r`nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==`r`nSec-WebSocket-Version: 13`r`n$extra`r`n"
    $b = [Text.Encoding]::ASCII.GetBytes($req); $s.Write($b, 0, $b.Length)
    $buf = New-Object byte[] 256; $n = $s.Read($buf, 0, 256); $c.Close()
    return [Text.Encoding]::ASCII.GetString($buf, 0, $n).Split("`r")[0]
}
$attackOrigin = Send-Raw "Origin: https://evil.example`r`n"
$attackHost = Send-Raw '' "evil.example:$port"

$eco1 = Start-Eco 1 14; $eco1.WaitForExit()
Start-Sleep -Seconds 7   # el navegador mide "se fue" mientras no hay traductor
$eco2 = Start-Eco 2 60; $eco2.WaitForExit()                            # termina por "olvidar apps"
$eco3 = Start-Eco 3 30; $eco3.WaitForExit()
$browser.WaitForExit()

$log = Get-ChildItem (Join-Path $data 'logs') -Filter 'session-*.jsonl' | Select-Object -First 1
$events = Get-Content $log.FullName | ForEach-Object { $_ | ConvertFrom-Json }
$results = @{}
foreach ($e in $events | Where-Object { $_.name -eq 'automation_js' }) {
    try { $r = ($e.result | ConvertFrom-Json) | ConvertFrom-Json } catch { continue }
    if ($r.fase) { $results[$r.fase] = $r.m }
}
$state = $events | Where-Object { $_.name -eq 'meeting_state' } | Select-Object -First 1
$pageState = if ($state) { $state.state | ConvertFrom-Json } else { $null }
$approvals = @($events | Where-Object { $_.name -eq 'api_approval' })
$asked = @($events | Where-Object { $_.name -eq 'api_approval_asked' })
$errors = @($events | Where-Object { $_.name -in 'automation_error', 'meeting_error', 'api_error' -or ($_.name -eq 'error') })
$grants = (Get-Content (Join-Path $data 'integrations.json') -Raw | ConvertFrom-Json).Grants
function Test-Tone($m) { $m -and $m.fraccion440 -gt 0.8 -and [math]::Abs($m.picoHz - 440) -le 10 }
function Test-Original($m) { $m -and $m.fraccion440 -lt 0.3 -and $m.nivelMax -gt 0.01 }

$checks = [ordered]@{
    'ataque: página web (Origin) -> 403'         = { $attackOrigin -match '403' }
    'ataque: Host ajeno -> 403'                  = { $attackHost -match '403' }
    'eco 1: pide permiso y se aprueba'           = { $approvals.Count -ge 1 -and $approvals[0].allowed -eq $true }
    'eco 1: en la reunión se oye el tono'        = { Test-Tone $results['api-tono'] }
    'eco 1: ida y vuelta < 100 ms'               = { $pageState -and $pageState.rttMs -ne $null -and $pageState.rttMs -lt 100 }
    'se fue: vuelve la voz original'             = { Test-Original $results['api-se-fue'] }
    'eco 2: con token entra sin preguntar'       = { $asked.Count -eq 2 -and (Test-Tone $results['api-token']) }
    'olvidar: corta a la app y vuelve la voz'    = { (Test-Original $results['olvidada']) -and $eco2.ExitCode -eq 0 -and @($grants).Count -eq 0 }
    'eco 3: token viejo -> pregunta, rechazado'  = { $eco3.ExitCode -eq 3 -and $approvals.Count -ge 2 -and $approvals[1].allowed -eq $false }
    'sin errores'                                = { $errors.Count -eq 0 }
}
$fail = $false
foreach ($k in $checks.Keys) {
    $ok = [bool](& $checks[$k])
    if (-not $ok) { $fail = $true }
    "{0,-44} {1}" -f $k, ($(if ($ok) { 'OK' } else { 'FALLA' }))
}
''
"ataques        Origin: $attackOrigin · Host: $attackHost"
foreach ($f in 'api-tono', 'api-se-fue', 'api-token', 'olvidada') { "{0,-14} {1}" -f $f, ($results[$f] | ConvertTo-Json -Compress) }
if ($pageState) { "latencia       ida y vuelta $($pageState.rttMs) ms (peor $($pageState.rttMaxMs)) + pedazo $($pageState.tapMs) ms + colchón $($pageState.jitterMs) ms + salida de audio $($pageState.outputLatencyMs) ms" }
"eco            salida: 1=$($eco1.ExitCode) 2=$($eco2.ExitCode) 3=$($eco3.ExitCode); pedidos de permiso: $($asked.Count)"
foreach ($e in $errors) { "error          $($e | ConvertTo-Json -Compress)" }
if ($Keep -or $fail) { "carpeta        $work" } else { Start-Sleep 3; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
''
if ($fail) { 'RESULTADO: FALLA'; exit 1 } else { 'RESULTADO: OK' }
