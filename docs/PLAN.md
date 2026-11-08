# Plan: Navegador liviano

Navegador para Windows que **se achica solo** cuando no lo estás usando y lo demuestra con números.
Motor: WebView2 (el Chromium que ya trae Windows) + .NET 8 WPF. Lo nuevo está en el shell, no en el motor.

## Principios
1. **Nada vivo que no se esté mirando.** Las pestañas de fondo bajan de nivel solas: activa → memoria baja → suspendida → descartada.
2. **Medir antes de afirmar.** Cada decisión deja un evento en un log JSONL que lee `tools/metrics` (`python -m metrics report`).
3. **Arranque liviano.** La sesión se restaura con todas las pestañas descartadas salvo la activa.
4. **Sin extensiones para lo básico.** El bloqueo de rastreo viene incluido y no cuesta nada en las peticiones normales.

## Las cuatro innovaciones y cómo se implementan

| Innovación | Implementación | Cómo se mide |
|---|---|---|
| Hibernación por foco e inactividad | Política pura (`HibernationPolicy`) evaluada cada 5 s. Pestaña de fondo inactiva `SuspendAfterSeconds` → `MemoryUsageTargetLevel=Low` + `TrySuspendAsync`. Inactiva `DiscardAfterMinutes` → se destruye el WebView2 y se guarda URL, título y scroll. Si la ventana pierde el foco `FocusLostSeconds`, se hace lo mismo con todas las de fondo y la activa pasa a memoria baja. No se toca la pestaña que reproduce audio. | Eventos `focus_lost`→`hibernate_done`, `focus_gained`→`tab_active_ready`; spans `suspend_tab`, `discard_tab`, `restore_tab` |
| Presupuesto de RAM | Cada muestra suma la memoria privada de los procesos de WebView2 del navegador (`GetProcessInfos`) más el proceso propio. Si supera `RamBudgetMb`, se descarta la pestaña de fondo usada hace más tiempo, una por ciclo. | `sample.app_private_mb`, evento `budget_discard` |
| Un proceso por sitio | Un único `CoreWebView2Environment` compartido con `--process-per-site`: todas las pestañas del mismo sitio comparten renderer. | `sample.app_procs` con N pestañas del mismo sitio |
| Bloqueo nativo de rastreo y publicidad | Dos capas: prevención de rastreo de Chromium en `Strict` y lista propia de dominios (`blocklist.txt`). Cada dominio se registra como filtro de WebView2, así que el evento sólo salta para peticiones bloqueables y las normales no pasan por código nuestro. | `sample.blocked_total`, contador en la barra de estado |

Nota de medición: los procesos se llaman `msedgewebview2.exe` y los comparten otras apps (Widgets, Teams). Por eso
`python -m metrics record --app msedgewebview2` mezcla procesos ajenos; la cifra válida es la que el navegador escribe en su propio log.

## Fases

| Fase | Contenido | Estado |
|---|---|---|
| 0 | Medidor (`tools/metrics`): sampler, eventos, estadísticas, benchmarks | Hecho (antes, en Linux y Windows) |
| 1 | Navegador usable: pestañas, barra de direcciones, atajos, ventanas nuevas como pestañas, pantalla completa, sesión, acceso directo | Esta entrega |
| 2 | Las cuatro innovaciones con log de métricas y barra de estado (RAM / presupuesto / dormidas / bloqueados) | Esta entrega |
| 3 | Uso real del usuario → leer logs → ajustar umbrales y arreglar lo que falle | Iterativo |
| 4 | Comparación formal contra Chrome con 20 pestañas (mismo set de URLs, misma duración) | Pendiente |
| 5 | Pulido: favoritos, historial, descargas propias, página de nueva pestaña, ajustes con UI, navegador predeterminado | Pendiente, según uso |

## Criterios de éxito (fase 4)
- Con 20 pestañas abiertas y 15 min de uso normal: memoria privada promedio ≤ 50 % de la de Chrome con las mismas pestañas.
- `focus_lost`→`hibernate_done` p95 < `FocusLostSeconds` + 1 s (la espera es a propósito); `focus_gained`→`tab_active_ready` p95 < 300 ms.
- Restaurar una pestaña descartada: p95 < 2 s en sitios comunes.
- Bloqueo: cero roturas de sitios reportadas en una semana de uso (si rompe, se saca el dominio de la lista).

## Ajustes (`%LOCALAPPDATA%\Navegador\settings.json`)
`RamBudgetMb` (1024), `SuspendAfterSeconds` (60), `DiscardAfterMinutes` (15), `FocusLostSeconds` (10),
`BlockingEnabled` (true), `SearchUrl` (Google), `AllowSites` (escudo), `BrowserArguments` (`--process-per-site`).
Se abren con el botón ⚙ y se releen al reiniciar.

## Riesgos conocidos
- **RivaTuner Statistics Server (MSI Afterburner)** inyecta `RTSSHooks64.dll` y tira el proceso principal de WebView2
  (`0xC0000005`, visto en 4 minidumps de crashpad el 2026-11-07). El navegador se recupera solo (pestaña restaurada en ~1 s)
  y avisa en la barra de estado; la solución es `tools/rtss-exclude.ps1` como administrador. Afecta a toda app con WebView2.
- WebView2 comparte el nombre de proceso `msedgewebview2.exe` con otras apps: medir siempre desde el log propio.

## Fuera de alcance por ahora
Motor propio, extensiones, sincronización entre equipos, perfiles múltiples, macOS/Linux.
