# Navegador

Navegador liviano para Windows que se achica solo cuando no lo usás. WebView2 (el Chromium que ya trae Windows) + .NET 8 WPF.
Plan, fases y criterios: [`docs/PLAN.md`](docs/PLAN.md).

## Qué tiene
- **Pestañas que se duermen**: una pestaña de fondo pasa a memoria baja y se suspende al minuto; a los 15 min se descarta (queda URL, título, scroll y un **snapshot**: al volver ves la foto al instante mientras la página real carga). Al perder el foco (juego, otra app) se duermen todas las de fondo a los 10 s. Nunca se toca la activa ni la que suena.
- **Presupuesto de RAM** (1 GB por defecto): si el navegador entero lo pasa, descarta de una vez las pestañas de fondo usadas hace más tiempo que hagan falta para volver al tope (estimando lo que ocupa cada una) y espera 15 s a que se libere la memoria. No descarta las que usaste en los últimos 3 min (`BudgetKeepRecentMinutes`), para no descartar y recargar en bucle, ni las que tienen un formulario que escribiste y no enviaste (lo que completa el sitio, como un buscador, no cuenta).
- **Un proceso por sitio** (`--process-per-site`): pestañas del mismo sitio comparten renderer.
- **Bloqueo de rastreo y publicidad** incluido: prevención de rastreo de Chromium en *Strict* + `blocklist.txt`. Las peticiones normales no pasan por código propio. El escudo de la barra de estado permite un sitio con un clic.
- **Arranque liviano**: la sesión se restaura con todas las pestañas dormidas salvo la activa.
- **Una sola instancia**: abrirlo otra vez (acceso directo, un link desde otra app, un `.html`) usa la ventana que ya está abierta; `--private` abre ahí una ventana privada. **Navegador predeterminado**: menú ⋮ → "Hacer navegador predeterminado…" lo registra (sin permisos de administrador) y abre Configuración, donde se elige (Windows no deja que un programa se elija solo). También `Navegador.exe --register-default` / `--unregister-default`.
- Barra de estado con RAM real / presupuesto, pestañas dormidas, bloqueos y zoom.
- **Controles**: menú ⋮ (nueva pestaña, ventana privada, favoritos, historial, descargas, zoom, buscar, imprimir, borrar datos, ajustes), estrella de favoritos, página de nueva pestaña con buscador, favoritos y recientes, historial (`about:history`), barra de búsqueda en la página, menú de pestaña con clic derecho (recargar, duplicar, dormir ahora, cerrar las demás).
- **Seguridad y privacidad**: HTTPS primero (vuelve a http sólo si el sitio no tiene HTTPS), notificaciones rechazadas, sin canales página→app, contraseñas sin guardar, SmartScreen, candado/advertencia en la barra, borrar datos, ventana privada que no trae nada de la normal (perfil propio, sin favoritos ni historial) y no deja actividad (ni log). Detalle en `docs/PLAN.md`.

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
Muestras cada 5 s (`app_private_mb`, `renderer_private_mb`, `cpu_pct` y `renderer_cpu_pct` —porcentaje de toda la máquina—, `app_procs`, pestañas por estado, bloqueados), spans (`startup`, `suspend_tab`, `discard_tab`, `restore_tab`), eventos `budget_discard`, `discard_skipped` (formulario sin enviar), `instance_request`, e intervalos `focus_lost→hibernate_done` y `focus_gained→tab_active_ready`.
No uses `metrics record --app msedgewebview2` para medir el navegador: ese nombre lo comparten otras apps.

Opciones: `Navegador.exe [--data-dir <carpeta>] [--private] [--automation <pasos.txt>] [url | archivo.html]` y `--register-default` / `--unregister-default`.
- `--data-dir`: otra carpeta de datos (pruebas, sin tocar tu perfil). Cada carpeta de datos es una instancia aparte.
- `--automation`: prueba de humo **oculta**: las ventanas se abren fuera de la pantalla, sin barra de tareas y sin tomar el foco; los menús no se abren (se dibujan en la captura); la app se cierra sola al terminar, aunque falle. El navegador ejecuta los pasos internamente (también arrancando con `--private`) y guarda capturas en `<datos>\shots\`. Pasos listos en `tools/smoke/` (`controles.txt`, `privacidad.txt`, `privada.txt`, `snapshot*.txt`, `formulario.txt`, `cpu-suspendida.txt`, `uso-real.txt`). Órdenes: `wait MS`, `go TEXTO`, `newtab [TEXTO]`, `tab N`, `sleep N`, `bookmark`, `find TEXTO`, `findclose`, `zoom +|-|0`, `history`, `menu`, `tabmenu N`, `closemenus`, `private`, `main`, `toprivate`, `js CÓDIGO`, `type TEXTO` (escribe en el campo enfocado con eventos de entrada reales), `cleardata`, `shot NOMBRE`, `close`. Las ventanas privadas no escriben en el log; sólo quedan los pasos y los resultados de `js` de la prueba. Nunca usar teclas o clics simulados sobre el escritorio real.
- Rastros de la ventana privada: `powershell -ExecutionPolicy Bypass -File tools/smoke/privada-rastros.ps1` (4 corridas ocultas; busca en toda la carpeta de datos; termina en `RESULTADO: OK` o `FALLA`).

## Estructura
| Carpeta | Qué |
|---|---|
| `src/Navegador.Core` | Lógica pura: política de hibernación, lista de bloqueo, barra de direcciones, HTTPS primero, favoritos/historial, páginas propias, ajustes, sesión, log |
| `src/Navegador` | App WPF + WebView2 |
| `tests/Navegador.Tests` | xUnit de `Navegador.Core` |
| `tools/metrics` | Medidor en Python (sampler, reportes, benchmarks) |
| `tools/*.ps1` | Ícono, acceso directo, estadísticas (`stats.ps1`) y RivaTuner (`rtss-*.ps1`) |
