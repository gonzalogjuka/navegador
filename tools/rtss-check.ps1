$root = 'D:\Repos\Navegador'
$data = "$root\.tmp\rtss-check"
Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
$p = Start-Process "$root\dist\Navegador.exe" -ArgumentList "--data-dir `"$data`" https://es.wikipedia.org" -PassThru
Start-Sleep 25   # RTSS espera InjectionDelay=15000 ms antes de inyectarse
$ids = @($p.Id) + (Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
    Where-Object { $_.CommandLine -match [regex]::Escape($data) } | ForEach-Object { $_.ProcessId })
foreach ($id in $ids) {
    $proc = Get-Process -Id $id -ErrorAction SilentlyContinue
    if (-not $proc) { continue }
    $hooked = $proc.Modules | Where-Object { $_.ModuleName -like 'RTSSHooks*' }
    $kind = (Get-CimInstance Win32_Process -Filter "ProcessId=$id").CommandLine -replace '.*--type=(\S+).*', '$1'
    if ($kind -notmatch '^[a-z-]+$') { $kind = 'principal' }
    '{0,-22} pid {1,-6} RTSS adentro: {2}' -f "$($proc.Name) ($kind)", $id, [bool]$hooked
}
$p.CloseMainWindow() | Out-Null; $p.WaitForExit(10000) | Out-Null
$log = Get-ChildItem "$data\logs\*.jsonl" | Select-Object -First 1
'Caídas en esta corrida: ' + (Select-String -Path $log -Pattern 'BrowserProcessExited').Count
