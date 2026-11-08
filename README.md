# Navegador

Navegador liviano para Windows que se achica solo cuando no lo usás. WebView2 (el Chromium que ya trae Windows) + .NET 8 WPF.
Plan, fases y criterios: [`docs/PLAN.md`](docs/PLAN.md).

## Qué tiene
- **Pestañas que se duermen**: una pestaña de fondo pasa a memoria baja y se suspende al minuto; a los 15 min se descarta (queda URL, título y scroll). Al perder el foco (juego, otra app) se duermen todas las de fondo a los 10 s. Nunca se toca la activa ni la que suena.
- **Presupuesto de RAM** (1 GB por defecto): si el navegador entero lo pasa, descarta la pestaña de fondo usada hace más tiempo.
- **Un proceso por sitio** (`--process-per-site`): pestañas del mismo sitio comparten renderer.
- **Bloqueo de rastreo y publicidad** incluido: prevención de rastreo de Chromium en *Strict* + `blocklist.txt`. Las peticiones normales no pasan por código propio. El escudo de la barra de estado permite un sitio con un clic.
- **Arranque liviano**: la sesión se restaura con todas las pestañas dormidas salvo la activa.
- Barra de estado con RAM real / presupuesto, pestañas dormidas y bloqueos.

Atajos: `Ctrl+T` nueva · `Ctrl+W` cerrar · `Ctrl+Shift+T` reabrir · `Ctrl+L`/`Alt+D`/`F6` dirección · `Ctrl+Tab` · `Ctrl+1..9` · `F5` · `Alt+←/→` · `F11` · clic del medio cierra la pestaña.

## Compilar, probar e instalar
```
dotnet test                                   # 30 tests de la lógica (Navegador.Core)
dotnet publish src/Navegador -c Release -o dist
powershell -ExecutionPolicy Bypass -File tools/install-shortcut.ps1   # acceso directo en el escritorio
```
Requiere .NET 8 Desktop Runtime y WebView2 Runtime (ambos vienen en Windows 11). Cerrá el navegador antes de publicar.

**Si usás MSI Afterburner / RivaTuner (RTSS):** RTSS intercepta DirectX dentro de WebView2 y lo hace caer de vez en cuando. El navegador se recupera solo y lo avisa en la barra de estado. Para que no pase más: `tools/rtss-exclude.ps1` en PowerShell **como administrador** (crea perfiles con `EnableHooking=0` para `msedgewebview2.exe` y `Navegador.exe`) y después `tools/rtss-restart.ps1` (pide permiso y reinicia Afterburner/RTSS, que lee los perfiles al arrancar). `tools/rtss-check.ps1` abre el navegador 8 veces y cuenta las caídas. `RTSSHooks64.dll` sigue apareciendo cargada en todo programa con ventana (hook global); eso es normal.

## Datos y métricas
Todo en `%LOCALAPPDATA%\Navegador`: `settings.json` (botón ⚙; se aplica al reiniciar), `session.json`, `WebView2\` (perfil) y `logs\session-*.jsonl`.
El log tiene el formato de `tools/metrics`. Resumen de la última sesión (crea el venv si falta):
```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/stats.ps1
```
A mano:
```
cd tools/metrics && .venv\Scripts\python -m metrics report %LOCALAPPDATA%\Navegador\logs\session-AAAAMMDD-HHMMSS.jsonl
```
Muestras cada 5 s (`app_private_mb`, `app_procs`, pestañas por estado, bloqueados), spans (`startup`, `suspend_tab`, `discard_tab`, `restore_tab`) e intervalos `focus_lost→hibernate_done` y `focus_gained→tab_active_ready`.
No uses `metrics record --app msedgewebview2` para medir el navegador: ese nombre lo comparten otras apps.

`Navegador.exe --data-dir <carpeta> [url]` usa otra carpeta de datos (pruebas).

## Estructura
| Carpeta | Qué |
|---|---|
| `src/Navegador.Core` | Lógica pura: política de hibernación, lista de bloqueo, barra de direcciones, ajustes, sesión, log |
| `src/Navegador` | App WPF + WebView2 |
| `tests/Navegador.Tests` | xUnit de `Navegador.Core` |
| `tools/metrics` | Medidor en Python (sampler, reportes, benchmarks) |
| `tools/*.ps1` | Ícono y acceso directo |
