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
| Hibernación por foco e inactividad | Política pura (`HibernationPolicy`) evaluada cada 1 s (la memoria se mide cada 5 s). Pestaña de fondo inactiva `SuspendAfterSeconds` → `MemoryUsageTargetLevel=Low` + `TrySuspendAsync`. Inactiva `DiscardAfterMinutes` → se destruye el WebView2 y se guarda URL, título y scroll. Si la ventana pierde el foco `FocusLostSeconds`, se hace lo mismo con todas las de fondo y la activa pasa a memoria baja. No se toca la pestaña que reproduce audio. | Eventos `focus_lost`→`hibernate_done`, `focus_gained`→`tab_active_ready`; spans `suspend_tab`, `discard_tab`, `restore_tab` |
| Presupuesto de RAM | Cada muestra suma la memoria privada de los procesos de WebView2 del navegador (`GetProcessInfos`) más el proceso propio. Si supera `RamBudgetMb`, descarta de una vez las pestañas de fondo menos usadas que hagan falta (memoria de renderers ÷ pestañas vivas = lo que ocupa cada una) y espera 15 s a que se libere, salvo que siga subiendo. Se salvan las `BudgetKeepRecentTabs` (2) usadas más recientemente si fue en los últimos `BudgetKeepRecentMinutes` (3) y las que tienen un formulario escrito sin enviar. | `sample.app_private_mb`, `sample.renderer_private_mb`, eventos `budget_discard`, `discard_skipped` |
| Un proceso por sitio | Un único `CoreWebView2Environment` compartido con `--process-per-site`: todas las pestañas del mismo sitio comparten renderer. | `sample.app_procs` con N pestañas del mismo sitio |
| Bloqueo nativo de rastreo y publicidad | Dos capas: prevención de rastreo de Chromium en `Strict` y lista propia de dominios (`blocklist.txt`). Cada dominio se registra como filtro de WebView2, así que el evento sólo salta para peticiones bloqueables y las normales no pasan por código nuestro. | `sample.blocked_total`, contador en la barra de estado |

Nota de medición: los procesos se llaman `msedgewebview2.exe` y los comparten otras apps (Widgets, Teams). Por eso
`python -m metrics record --app msedgewebview2` mezcla procesos ajenos; la cifra válida es la que el navegador escribe en su propio log.

### Comprobado (2026-11-07, pruebas ocultas con `--automation`)
| Qué | Resultado |
|---|---|
| Proceso por sitio | 4 pestañas de Wikipedia: **6 procesos y 660 MB** con `--process-per-site` contra **9 procesos y 778 MB** sin el flag (−3 procesos, −118 MB). Con 3 sitios distintos más, 10 contra 12 procesos. |
| Presupuesto de RAM | Con tope 300 MB y 4 pestañas, bajó de 812 MB a 337 MB descartando 3 (prueba de 0.1.0). |
| Descartar y despertar | Descartar: ~16 ms. Despertar: 0.5–1.7 s. El scroll vuelve exacto (2000 px → 2000 px), también después de reiniciar el navegador. |
| Snapshot (0.3.0) | Foto JPEG de la pestaña activa (2 s después de cargar y cada 20 s, junto con el scroll): 30–66 ms en caliente, ~150 KB. Al despertar se ve la foto al instante con "Despertando…" y la página real la reemplaza sin salto. Tras reiniciar, la foto aparece con el navegador en 151 MB. "Borrar datos" elimina la carpeta; la ventana privada no escribe ninguna. |
| Sesión liviana | 3 pestañas guardadas: al reabrir carga sólo la activa; las otras 2 quedan descartadas. |
| Foco | `focus_lost`→`hibernate_done` 2.2–2.7 s con espera configurada de 2 s. |
| Bloqueo | 2–11 peticiones de rastreo bloqueadas por página en sitios de noticias. |
| Tope de RAM (0.4.0) | 9 sitios, tope 700 MB: antes descartaba de a una cada 5 s (8 de 18 muestras sobre el tope); de a varias, 4 de 18. Alternando 2 pestañas con tope 450 MB: antes 14 descartes y 12 recargas (6,2 s) sin bajar la memoria (528 contra 533 MB promedio); ahora 0 y 0. Contra: tras abrir muchas pestañas de golpe protege las 2 últimas y puede quedar por encima del tope hasta 3 min (9 sitios: 843 MB con tope 700). |
| Formularios (0.4.0) | `tools/smoke/formulario.txt`: con tope 128 MB, la pestaña con texto escrito no se descarta (`discard_skipped`) y el texto sigue al volver; las demás sí. |
| CPU (0.4.0) | `tools/smoke/cpu-suspendida.txt`: pestaña con un worker en bucle = 16,5 % de la máquina (un núcleo de 6) activa y de fondo; suspendida 0 %; al volver, 16 %. Una pestaña de fondo no suspendida sigue gastando CPU hasta que pasa `SuspendAfterSeconds`. |
| Instancia única (0.4.0) | Segundo arranque con URL, con `--private` y con un `.html`: le pasa el pedido a la instancia abierta y termina en 202–222 ms; un solo log; la pestaña abre (`file:///…x.html`, título leído). |
| Contra Chrome y Edge (0.4.0) | 20 pestañas abiertas cada 3 s y 15 min quietas, PC libre (CPU del sistema 15–37 %), memoria privada de todo el árbol de procesos cada 10 s (`tools/compare`). Promedio: Navegador **851 MB** (máx. 1623, final 800), Chrome for Testing 157 **3694 MB**, Edge **3533 MB** → 0,23 y 0,24: cumple el ≤ 0,50. Navegador sin hibernar ni tope: 2140 MB (0,58 de Chrome; esa diferencia sería del proceso por sitio y del bloqueo, no se midió por separado). Cómo lo logra: el tope de 1 GB descartó 16 pestañas (18 descartes en total) y quedaron 800 MB con pocas vivas (GPU 207 MB, renderers 360, ventana 92, navegador 94). Contras de la medición: Chrome y Edge corren sin ventana (les juega a favor) y sin bloqueador (les juega en contra); Chrome/Edge mantienen las 20 pestañas vivas y Navegador recarga las descartadas al volver a ellas (0,5–2 s). |
| Sólo con tests unitarios | No dormir la pestaña que suena (`HibernationPolicy`): no se probó con audio real para no hacer ruido en la PC. Pantalla completa (F11 / videos): sin probar. |

## Fases

| Fase | Contenido | Estado |
|---|---|---|
| 0 | Medidor (`tools/metrics`): sampler, eventos, estadísticas, benchmarks | Hecho (antes, en Linux y Windows) |
| 1 | Navegador usable: pestañas, barra de direcciones, atajos, ventanas nuevas como pestañas, pantalla completa, sesión, acceso directo | Hecho (0.1.0) |
| 2 | Las cuatro innovaciones con log de métricas y barra de estado (RAM / presupuesto / dormidas / bloqueados) | Hecho (0.1.0) |
| 3 | Uso real del usuario → leer logs → ajustar umbrales y arreglar lo que falle | Iterativo |
| 4 | Comparación formal contra Chrome y Edge con 20 pestañas (mismo set de URLs, misma duración): `tools/compare/` | **Hecho (0.4.0, 2026-11-08): cumple.** Promedio en 15 min: Navegador 851 MB, Chrome 3694 MB (0,23), Edge 3533 MB (0,24); sin hibernar ni tope 2140 MB (0,58 de Chrome). Detalle en `tools/compare/resultados/20261108-160743-libre.md` |
| 5a | Controles: menú ⋮, favoritos (estrella, Ctrl+D), historial (about:history), página de nueva pestaña, buscar en la página, zoom, imprimir, descargas (diálogo de WebView2), menú de pestaña (recargar, duplicar, dormir ahora, cerrar otras), ventana privada | Hecho (0.2.0) |
| 5b | Seguridad y privacidad: ver tabla abajo | Hecho (0.2.0) |
| 5c | Una sola instancia y registro como navegador predeterminado | Hecho (0.4.0) |
| 5d | Ajustes con interfaz (hoy: settings.json), importar favoritos de Chrome/Edge, segunda ventana normal (Ctrl+N), sugerencias en la barra de direcciones, GPC | Pendiente, según uso |

## Seguridad y privacidad (0.2.0)

| Medida | Cómo |
|---|---|
| HTTPS primero | http:// se reintenta como https://; si falla (cualquier error salvo cancelación) o el sitio redirige a http, el dominio y sus subdominios quedan en http por esa corrida. No toca redes locales ni puertos explícitos (`HttpsUpgrade`). |
| Notificaciones | Rechazadas sin preguntar (`BlockNotifications`). Cámara, micrófono y ubicación los pregunta WebView2. |
| Superficie expuesta | `AreHostObjectsAllowed=false`, `IsWebMessageEnabled=false`: las páginas no tienen ningún canal hacia la app. |
| Contraseñas | No se guardan en el perfil (`IsPasswordAutosaveEnabled=false`); usar un gestor. |
| SmartScreen | `IsReputationCheckingRequired=true` contra sitios y descargas maliciosas. |
| Rastreo | Prevención Strict + lista de dominios (ya en 0.1.0). |
| Indicador de conexión | Candado en https, advertencia naranja en http. |
| Borrar datos | Menú ⋮ o Ctrl+Shift+Supr: cookies, caché, almacenamiento de sitios e historial. |
| Ventana privada | Perfil propio de WebView2 (`Privada-<aleatorio>`) siempre en modo InPrivate: no hereda nada del perfil normal (cookies, almacenamiento, zoom por sitio, permisos). No ve favoritos ni historial; HTTPS primero y "permitir sitio" en memoria de esa ventana. No escribe log, historial, sesión, snapshots, ajustes ni `data.json`. Al cerrar la última ventana privada el perfil se manda a borrar y la próxima usa otro; al salir de la app se espera al proceso de WebView2 y se borra la carpeta; restos de un cierre forzado se borran al arrancar. Pruebas: `tools/smoke/privada.txt` y `tools/smoke/privada-rastros.ps1` (busca una marca visitada en privada en toda la carpeta de datos). |
| Páginas propias | Nueva pestaña e historial sin JavaScript, con CSP `default-src 'none'`; títulos y URLs de sitios siempre escapados; sólo se enlaza http/https. |
| Snapshots | Imágenes de páginas en `snapshots\` con nombres aleatorios (no revelan la URL); `session.json` sólo acepta nombres generados por la app. Se borran al cerrar la pestaña, con "Borrar datos" y al arrancar si quedaron huérfanos. La ventana privada nunca los escribe. |

Fuera por ahora: cabecera `Sec-GPC` (WebView2 no permite agregar cabeceras sin interceptar todas las peticiones, lo que cuesta rendimiento).

## Criterios de éxito (fase 4)
- Con 20 pestañas abiertas y 15 min de uso normal: memoria privada promedio ≤ 50 % de la de Chrome con las mismas pestañas.
- `focus_lost`→`hibernate_done` p95 < `FocusLostSeconds` + 1 s (la espera es a propósito); `focus_gained`→`tab_active_ready` p95 < 300 ms.
- Restaurar una pestaña descartada: p95 < 2 s en sitios comunes.
- Bloqueo: cero roturas de sitios reportadas en una semana de uso (si rompe, se saca el dominio de la lista).

## Ajustes (`%LOCALAPPDATA%\Navegador\settings.json`)
`RamBudgetMb` (1024), `SuspendAfterSeconds` (60), `DiscardAfterMinutes` (15), `FocusLostSeconds` (10),
`BlockingEnabled` (true), `SearchUrl` (Google), `AllowSites` (escudo), `BrowserArguments` (`--process-per-site`),
`HttpsFirst` (true), `BlockNotifications` (true). Se abren desde el menú ⋮ → Ajustes y se releen al reiniciar.

## Riesgos conocidos
- **RivaTuner Statistics Server (MSI Afterburner)** inyecta `RTSSHooks64.dll` y tira el proceso principal de WebView2
  (`0xC0000005`, visto en 4 minidumps de crashpad el 2026-11-07). El navegador se recupera solo (pestaña restaurada en ~1 s)
  y avisa en la barra de estado; la solución es `tools/rtss-exclude.ps1` como administrador. Afecta a toda app con WebView2.
- WebView2 comparte el nombre de proceso `msedgewebview2.exe` con otras apps: medir siempre desde el log propio.

## Fuera de alcance por ahora
Motor propio, extensiones, sincronización entre equipos, perfiles múltiples, macOS/Linux.
