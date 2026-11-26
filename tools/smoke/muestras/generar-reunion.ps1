# Arma una grabación de prueba con frases de reunión en español (voz de Windows «Sabina», es-MX), con nombres propios
# que el reconocedor no conoce (Supabase, Vercel, Terraform, Kubernetes, Control Cloud). Sirve para medir la calidad y la
# demora del traductor sin navegador:
#   powershell -ExecutionPolicy Bypass -File tools/smoke/muestras/generar-reunion.ps1 [-Out es-reunion.wav]
#   dist\voz\astro-voz.exe --file es-reunion.wav --models %LOCALAPPDATA%\Navegador\modelos --data CARPETA --out r.json
# (en CARPETA\voz-glosario.txt van los nombres, uno por línea; sin glosario salen "database", "Bercel", "government").
# Es voz sintética: sirve para comparar una versión con otra, no reemplaza una prueba con voz real.
param([string]$Out = (Join-Path $env:TEMP 'es-reunion.wav'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoice('Microsoft Sabina Desktop')
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000,
    [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$lines = @(
    'Hola a todos, gracias por sumarse a la reunión.',
    'Hoy quiero mostrarles el avance del proyecto y contarles qué nos falta para salir a producción.',
    'La semana pasada migramos la base de datos a Supabase y desplegamos el frontend en Vercel.',
    'Tuvimos un problema con el pipeline de Terraform, pero ya lo resolvimos.',
    'Si les parece bien, la próxima semana empezamos con las pruebas de carga en Kubernetes.',
    'Me gustaría saber qué opinan del presupuesto, porque creo que nos estamos quedando cortos.',
    'No estoy seguro de que lleguemos a tiempo, salvo que sumemos a otra persona al equipo.',
    '¿Alguien tiene alguna pregunta antes de que sigamos?',
    'Perfecto, entonces quedamos así: les mando el resumen por mail y nos vemos el jueves.',
    'Control Cloud te deja ver y operar toda tu infraestructura desde un solo lugar.',
    'Lo que más me preocupa es el costo de las máquinas virtuales, que el mes pasado se nos fue de las manos, así que armé un tablero para seguirlo día a día y avisarnos cuando pasamos el límite.'
)
$ssml = '<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="es-MX">' +
    (($lines | ForEach-Object { [System.Security.SecurityElement]::Escape($_) + '<break time="900ms"/>' }) -join '') + '</speak>'
$synth.SetOutputToWaveFile($Out, $format)
$synth.SpeakSsml($ssml)
$synth.Dispose()
"listo: $Out"
