# Reinicia RivaTuner Statistics Server para que lea los perfiles nuevos (RTSS los lee al arrancar).
# Se eleva solo: Windows pide permiso de administrador.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools/rtss-restart.ps1
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}
Get-Process MSIAfterburner, RTSS, RTSSHooksLoader64, RTSSHooksLoader, EncoderServer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
$ab = Join-Path ${env:ProgramFiles(x86)} 'MSI Afterburner\MSIAfterburner.exe'
$rtss = Join-Path ${env:ProgramFiles(x86)} 'RivaTuner Statistics Server\RTSS.exe'
if (Test-Path $ab) { Start-Process $ab } elseif (Test-Path $rtss) { Start-Process $rtss }
