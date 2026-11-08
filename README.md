# Navegador

Navegador liviano para Windows que se achica solo cuando no lo usás. WebView2 (el Chromium que ya trae Windows) + .NET 8 WPF.
Plan, fases y criterios: [`docs/PLAN.md`](docs/PLAN.md).

## Qué tiene
- **Pestañas que se duermen**: una pestaña de fondo pasa a memoria baja y se suspende al minuto; a los 15 min se descarta (queda URL, título y scroll). Al perder el foco (juego, otra app) se duermen todas las de fondo a los 10 s. Nunca se toca la activa ni la que suena.
- **Presupuesto de RAM** (1 GB por defecto): si el navegador entero lo pasa, descarta la pestaña de fondo usada hace más tiempo.
- **Un proceso por sitio** (`--process-per-site`): pestañas del mismo sitio comparten renderer.
- **Bloqueo de rastreo y publicidad** incluido: prevención de rastreo de Chromium en *Strict* + `blocklist.txt`. Las peticiones normales no pasan por código propio. El escudo de la barra de estado permite un sitio con un clic.
- **Arranque liviano**: la sesión se restaura con todas las pestañas dormidas salvo la activa.
- Barra de estado con RAM real / presupuesto, pestañas dormidas, bloqueos y zoom.
- **Controles**: menú ⋮ (nueva pestaña, ventana privada, favoritos, historial, descargas, zoom, buscar, imprimir, borrar datos, ajustes), estrella de favoritos, página de nueva pestaña con buscador, favoritos y recientes, historial (`about:history`), barra de búsqueda en la página, menú de pestaña con clic derecho (recargar, duplicar, dormir ahora, cerrar las demás).
- **Seguridad y privacidad**: HTTPS primero (vuelve a http sólo si el sitio no tiene HTTPS), notificaciones rechazadas, sin canales página→app, contraseñas sin guardar, SmartScreen, candado/advertencia en la barra, borrar datos, ventana privada. Detalle en `docs/PLAN.md`.

Atajos: `Ctrl+T` nueva · `Ctrl+W` cerrar · `Ctrl+Shift+T` reabrir · `Ctrl+Shift+N` ventana privada · `Ctrl+L`/`Alt+D`/`F6` dirección · `Ctrl+D` favorito · `Ctrl+H` historial · `Ctrl+J` descargas · `Ctrl+F` buscar · `Ctrl+P` imprimir · `Ctrl++`/`Ctrl+-`/`Ctrl+0` zoom · `Ctrl+Shift+Supr` borrar datos · `Ctrl+Tab` · `Ctrl+1..9` · `F5` · `Alt+←/→` · `F11` · clic del medio cierra la pestaña.

## Compilar, probar e instalar
```
dotnet test                                   # tests de la lógica (Navegador.Core)
dotnet publish src/Navegador -c Release -o dist
powershell -ExecutionPolicy Bypass -File tools/install-shortcut.ps1   # acceso directo en el escritorio
```
Requiere .NET 8 Desktop Runtime y WebView2 Runtime (ambos vienen en Windows 11). Cerrá el navegador antes de publicar.

**Si usás MSI Afterburner / RivaTuner (RTSS):** RTSS intercepta DirectX dentro de WebView2 y lo hace caer de vez en cuando. El navegador se recupera solo y lo avisa en la barra de estado. Para que no pase más: `tools/rtss-exclude.ps1` en PowerShell **como administrador** (crea perfiles con `EnableHooking=0` para `msedgewebview2.exe` y `Navegador.exe`) y después `tools/rtss-restart.ps1` (pide permiso y reinicia Afterburner/RTSS, que lee los perfiles al arrancar). `tools/rtss-check.ps1` abre el navegador 8 veces y cuenta las caídas. `RTSSHooks64.dll` sigue apareciendo cargada en todo programa con ventana (hook global); eso es normal.

## Datos y métricas
Todo en `%LOCALAPPDATA%\Navegador`: `settings.json` (menú ⋮ → Ajustes; se aplica al reiniciar), `session.json`, `data.json` (favoritos e historial), `WebView2\` (perfil) y `logs\session-*.jsonl`.
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

Opciones: `Navegador.exe [--data-dir <carpeta>] [--private] [--automation <pasos.txt>] [url]`.
- `--data-dir`: otra carpeta de datos (pruebas, sin tocar tu perfil).
- `--automation`: prueba de humo **sin teclas simuladas**; el navegador ejecuta los pasos internamente y guarda capturas en `<datos>\shots\` aunque la ventana esté tapada. Órdenes: `wait MS`, `go TEXTO`, `newtab [TEXTO]`, `tab N`, `sleep N`, `bookmark`, `find TEXTO`, `findclose`, `zoom +|-|0`, `history`, `menu`, `tabmenu N`, `closemenus`, `private`, `main`, `js CÓDIGO`, `cleardata`, `shot NOMBRE`, `close`. No usar SendKeys sobre el escritorio real: las teclas pueden ir a otra ventana.

## Estructura
| Carpeta | Qué |
|---|---|
| `src/Navegador.Core` | Lógica pura: política de hibernación, lista de bloqueo, barra de direcciones, HTTPS primero, favoritos/historial, páginas propias, ajustes, sesión, log |
| `src/Navegador` | App WPF + WebView2 |
| `tests/Navegador.Tests` | xUnit de `Navegador.Core` |
| `tools/metrics` | Medidor en Python (sampler, reportes, benchmarks) |
| `tools/*.ps1` | Ícono, acceso directo, estadísticas (`stats.ps1`) y RivaTuner (`rtss-*.ps1`) |
