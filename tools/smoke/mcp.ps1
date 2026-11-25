# Prueba oculta del servidor MCP (fase 6.3): navegador-mcp.exe hablado por stdio como lo haría un cliente de LLM,
# contra un navegador oculto (--automation, carpeta temporal, API en un puerto libre) que además tiene abierta una
# ventana privada. No usa ningún LLM: los pedidos MCP están escritos a mano.
#   powershell -ExecutionPolicy Bypass -File tools/smoke/mcp.ps1 [-Exe dist\Navegador.exe] [-Keep]
# Controles: initialize y tools/list; el permiso se pide en el navegador (captura "permiso-mcp") y se aprueba;
# list_tabs no muestra la ventana privada; read_page lee la página; open_url rechaza file:, javascript: y data: y abre
# https; set_meeting_mode activa el modo reunión; una herramienta inexistente da error.
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\dist\Navegador.exe'), [switch]$Keep)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$page = ([Uri](Resolve-Path (Join-Path $PSScriptRoot 'reunion.html')).Path).AbsoluteUri
$work = Join-Path $env:TEMP ("nav-mcp-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$data = Join-Path $work 'datos'
New-Item -ItemType Directory $data | Out-Null
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start()
$port = $probe.LocalEndpoint.Port; $probe.Stop()
@{ BrowserArguments = '--process-per-site --use-fake-device-for-media-stream --autoplay-policy=no-user-gesture-required'; ApiPort = $port } |
    ConvertTo-Json | Set-Content (Join-Path $data 'settings.json') -Encoding UTF8

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
& $dotnet build (Join-Path $repo 'src\Navegador.Mcp') -c Release -o (Join-Path $work 'mcp') --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'No compila src\Navegador.Mcp' }

$steps = "go $page`nwait 1500`nprivate`nwait 1500`nmain`nwaitapproval`nshot permiso-mcp`napiapprove`nwait 25000`nclose`n"
$stepsFile = Join-Path $work 'pasos.txt'
Set-Content -Path $stepsFile -Value $steps -Encoding UTF8
$browser = Start-Process -FilePath $Exe -ArgumentList @('--data-dir', "`"$data`"", '--automation', "`"$stepsFile`"") -PassThru
for ($i = 0; $i -lt 100 -and -not (Test-Path (Join-Path $data 'api.json')); $i++) { Start-Sleep -Milliseconds 100 }

$psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $work 'mcp\navegador-mcp.exe'), "--data-dir `"$data`" --token-file `"$(Join-Path $work 'mcp-token.txt')`"")
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$mcp = [Diagnostics.Process]::Start($psi)
$stdin = [IO.StreamWriter]::new($mcp.StandardInput.BaseStream, [Text.UTF8Encoding]::new($false)); $stdin.AutoFlush = $true
$script:nextId = 0
function Invoke-Mcp([string]$method, $params) {
    $id = ++$script:nextId
    $stdin.WriteLine((@{ jsonrpc = '2.0'; id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress))
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        $task = $mcp.StandardOutput.ReadLineAsync()
        if (-not $task.Wait(40000)) { break }
        $r = $task.Result | ConvertFrom-Json
        if ($r.id -eq $id) { return $r }
    }
    throw "sin respuesta a $method"
}
function Invoke-Tool([string]$name, $arguments = @{}) {
    $r = Invoke-Mcp 'tools/call' @{ name = $name; arguments = $arguments }
    [pscustomobject]@{ Error = [bool]$r.result.isError; Text = $r.result.content[0].text }
}

$results = [ordered]@{}
$init = Invoke-Mcp 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'prueba'; version = '1' } }
$stdin.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$tools = (Invoke-Mcp 'tools/list' @{}).result.tools.name
$list1 = Invoke-Tool 'list_tabs'                                      # espera el permiso
$tabs1 = $list1.Text | ConvertFrom-Json
$read = Invoke-Tool 'read_page'
$badFile = Invoke-Tool 'open_url' @{ url = 'file:///C:/Windows/win.ini' }
$badJs = Invoke-Tool 'open_url' @{ url = 'javascript:alert(1)' }
$badData = Invoke-Tool 'open_url' @{ url = 'data:text/html,hola' }
$open = Invoke-Tool 'open_url' @{ url = 'https://example.com/' }
Start-Sleep -Seconds 3
$reunionTab = ($tabs1 | Where-Object { $_.url -like 'file:*reunion.html' }).id
$meeting = Invoke-Tool 'set_meeting_mode' @{ on = $true; tab = $reunionTab }
$back = Invoke-Tool 'activate_tab' @{ tab = $reunionTab }
$tabs2 = (Invoke-Tool 'list_tabs').Text | ConvertFrom-Json
$unknown = Invoke-Tool 'borrar_todo'
$stdin.Close(); $mcp.WaitForExit(5000) | Out-Null
$stderr = $mcp.StandardError.ReadToEnd()
$browser.WaitForExit()

$log = Get-ChildItem (Join-Path $data 'logs') -Filter 'session-*.jsonl' | Select-Object -First 1
$events = Get-Content $log.FullName | ForEach-Object { $_ | ConvertFrom-Json }
$errors = @($events | Where-Object { $_.name -in 'automation_error', 'api_error', 'error' })
$approval = $events | Where-Object { $_.name -eq 'api_approval' } | Select-Object -First 1

$checks = [ordered]@{
    'initialize y tools/list (5 herramientas)'     = { $init.result.serverInfo.name -eq 'navegador' -and @($tools).Count -eq 5 }
    'pide permiso en el navegador y se aprueba'     = { $approval -and $approval.allowed -and $approval.app -eq 'navegador-mcp' }
    'list_tabs: sólo la ventana normal'             = { -not $list1.Error -and @($tabs1).Count -eq 1 -and $reunionTab }
    'read_page: lee el texto de la página'          = { -not $read.Error -and $read.Text -match 'Reunión de prueba' }
    'open_url: rechaza file:, javascript:, data:'   = { $badFile.Error -and $badJs.Error -and $badData.Error }
    'open_url: abre https'                          = { -not $open.Error -and (@($tabs2 | Where-Object { $_.url -like 'https://example.com*' }).Count -eq 1) }
    'set_meeting_mode: activa el modo reunión'      = { -not $meeting.Error -and ($tabs2 | Where-Object { $_.id -eq $reunionTab }).meeting -eq $true }
    'activate_tab: vuelve a la pestaña'             = { -not $back.Error -and ($tabs2 | Where-Object { $_.id -eq $reunionTab }).active -eq $true }
    'herramienta inexistente: error'                = { $unknown.Error }
    'sin errores en el navegador'                   = { $errors.Count -eq 0 }
}
$fail = $false
foreach ($k in $checks.Keys) {
    $ok = [bool](& $checks[$k])
    if (-not $ok) { $fail = $true }
    "{0,-46} {1}" -f $k, ($(if ($ok) { 'OK' } else { 'FALLA' }))
}
''
"herramientas   $($tools -join ', ')"
"pestañas       antes: $(@($tabs1).Count) · después: $(($tabs2 | ForEach-Object { "$($_.id) $($_.url) activa=$($_.active) reunion=$($_.meeting)" }) -join ' | ')"
"rechazos       file: $($badFile.Text) · javascript: $($badJs.Text)"
"read_page      $(($read.Text -split "`n" | Select-Object -First 3) -join ' / ')"
foreach ($e in $errors) { "error          $($e | ConvertTo-Json -Compress)" }
if ($stderr) { "mcp stderr     $($stderr.Trim())" }
if ($Keep -or $fail) { "carpeta        $work" } else { Start-Sleep 3; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
''
if ($fail) { 'RESULTADO: FALLA'; exit 1 } else { 'RESULTADO: OK' }
