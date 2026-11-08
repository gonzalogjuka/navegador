# Resumen de la última sesión del navegador (o de la que se pase) con tools/metrics.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools/stats.ps1 [-Log ruta] [-DataDir carpeta]
param([string]$Log, [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'Navegador'))
$metrics = Join-Path $PSScriptRoot 'metrics'
$py = Join-Path $metrics '.venv\Scripts\python.exe'
if (-not (Test-Path $py)) {
    py -m venv (Join-Path $metrics '.venv')
    & $py -m pip install -q -r (Join-Path $metrics 'requirements.txt')
}
if (-not $Log) {
    $Log = Get-ChildItem (Join-Path $DataDir 'logs\session-*.jsonl') | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $Log) { throw "No hay logs en $DataDir\logs" }
$Log = (Resolve-Path $Log).Path
Push-Location $metrics
try { & $py -m metrics report $Log } finally { Pop-Location }
'--- errores, caídas y bloqueos de sitio:'
Select-String -Path $Log -Pattern '"name": ?"(error|process_failed|recovery_gave_up|site_allowed|site_blocked)"' | ForEach-Object { $_.Line }
