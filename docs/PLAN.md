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
| Presupuesto de RAM | Cada muestra suma la memoria privada de los procesos de WebView2 del navegador (`GetProcessInfos`) más el proceso propio. **Antes de cargar** (abrir o despertar una pestaña, ir a otro sitio) hace lugar para lo que va a ocupar la página: lo que sumó la última carga de ese sitio (`PageCosts`, sólo en memoria) o el promedio de las páginas vivas. **Si igual se pasa**, descarta de una vez las pestañas de fondo menos usadas que hagan falta para quedar en el 85 % del tope (memoria de páginas —renderers + GPU— ÷ pestañas vivas = lo que ocupa cada una) y espera 15 s a que se libere, salvo que siga subiendo. Las `BudgetKeepRecentTabs` (2) usadas más recientemente si fue en los últimos `BudgetKeepRecentMinutes` (3) se descartan al final; nunca la activa, la que suena ni las que tienen un formulario que escribiste sin enviar. Suspender no alcanza: una pestaña suspendida conserva su renderer y su memoria de GPU. | `sample.app_private_mb`, `sample.renderer_private_mb`, `sample.gpu_private_mb`, eventos `make_room`, `budget_discard` (`reason` budget/room), `discard_skipped` |
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
| Formularios (0.4.0, 0.4.1) | `tools/smoke/formulario.txt`: con tope 128 MB, la pestaña donde se escribió texto (orden `type`, entrada real) no se descarta (`discard_skipped`) y el texto sigue al volver; la que tiene un campo completado por la página sí se descarta, igual que las demás. En 0.4.0 los buscadores completados por el sitio (Google, Mercado Libre, Maps) contaban como formulario escrito. |
| Uso simulado (0.4.1 → 0.4.2) | `tools/smoke/uso-real.txt`, 11 pestañas en ~7 min. 0.4.1: promedio 1115 MB, 71 % de las muestras sobre el tope de 1 GB, quedaba en ~1,2 GB con 1 viva, 2 suspendidas y 8 descartadas (la GPU pasa de 200 a 450–500 MB con Google Maps y no baja mientras Maps está suspendida). 0.4.2 (las recientes se descartan al final, lugar antes de cargar con costo aprendido por sitio, meta 85 %): promedio **653 MB**, **6 %** sobre el tope (sólo mientras carga Maps), en reposo 490–640 MB, mismos 8 despertares, 0 errores. |
| Límite del tope (0.4.2) | Piso fijo ~380 MB (ventana ~95, navegador ~50, utilidades ~40, GPU ~200 tras el primer uso). Maps o YouTube solos: ~1050 MB. Alternando cada 10 s entre Maps, YouTube y La Nación con tope 1 GB: 1 viva, 9 recargas en 9 cambios (0,36–1,14 s, foto al instante), 59 % de las muestras sobre el tope. Un descarte devuelve la memoria en ~10 s. |
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
| 6 | Integraciones y modo reunión (ver abajo) | En curso (desde 2026-11-25) |

## Fase 6: integraciones y modo reunión (2026-11-25)

Rumbo: gratis para todos y abierto a integraciones (Claude, otros LLM, apps propias). Objetivo que guía la fase:
**en una reunión web (Meet, Teams web, Zoom web, Jitsi) hablás en español y la otra persona te escucha en inglés**,
dando permiso de cámara y micrófono como siempre. La traducción la hace una app externa (la de Gonzalo, que se adapta
después); el navegador pone la plomería.

| # | Qué | Estado |
|---|---|---|
| 6.1 | **Micrófono virtual** (spike): en la pestaña con modo reunión, `getUserMedia` devuelve una pista de audio armada por el navegador que mezcla tu voz original y la voz traducida que llega de la app; tu voz va a la app. Sin traductor conectado pasa la voz original (la reunión nunca queda muda). La pestaña en reunión nunca se duerme ni se descarta. | Hecho, sólo con prueba oculta (`tools/smoke/reunion.ps1`, 10/10). Falta: reunión real (Meet/Teams/Zoom) |
| 6.2 | **API local (capa B)**: WebSocket en `127.0.0.1` sólo para apps de la PC (se rechaza toda conexión con `Origin`, o sea páginas web); cada app pide permiso una vez y se aprueba/revoca en el navegador. Expone el audio del modo reunión (mic → app, voz traducida → reunión). Cliente de ejemplo en `tools/integraciones/Eco`; protocolo en `docs/API.md`. Pestañas y texto de la página pasan a 6.3. | Hecho (audio), con prueba oculta (`tools/smoke/integracion.ps1`, 10/10). Falta: app de traducción real |
| 6.3 | **Servidor MCP** sobre la misma API, para que Claude u otro LLM maneje el navegador con permisos. | Hecho: `navegador-mcp.exe` (ver, leer, abrir, cambiar de pestaña, modo reunión), prueba oculta `tools/smoke/mcp.ps1` 10/10. Fuera por ahora: clics y escribir en formularios. Falta: probarlo con Claude Desktop/Code |
| 6.4 | **Audio entrante**: capturar lo que dicen los demás (captura de audio por proceso de Windows 11) y mostrar subtítulos traducidos. | Pendiente |
| 6.5 | **Panel lateral (capa C)**: integraciones como páginas web con manifiesto y permisos, que se duermen como una pestaña. | Pendiente |
| 6.6 | **Extensiones de Chrome (capa A)**: soporte de WebView2 + página para instalar desde carpeta, activar y quitar. | Pendiente |

### Fase 7: listo para usuarios no técnicos, de punta a punta (decidido 2026-11-25)

**Objetivo único: doble clic en el instalador → entrar a una Meet → dar permisos de cámara y micrófono → la otra
persona te escucha en inglés.** Sin compilar, sin configurar, sin conectar apps, sin botones.

| # | Qué | Decisión |
|---|---|---|
| 7.1 | **Instalador** `NavegadorSetup.exe`: publicación autocontenida (no pide .NET), instala en `%LOCALAPPDATA%\Programs\Navegador`, accesos directos, desinstalar desde Configuración de Windows (HKCU, sin admin). | — |
| 7.2 | **Modo reunión automático** en Meet, Teams web, Zoom web y Whereby: sin botón ni recarga (el botón queda para otros sitios). | Hecho (también Jitsi); probado en meet.jit.si |
| 7.3 | **Traductor incorporado**: el motor de `gonzalogjuka/voz-en` (traducción es→en y voz inglesa Kokoro en WebGPU/WASM, panel de debug) **copiado** a este repo y adaptado; `voz-en` no se toca. Corre en una página interna oculta y arranca solo cuando la reunión toma el micrófono. | Hecho en `voz/`: Whisper base (GPU) + voz de Windows; 1,3 s mediana. Falta bajar RAM (~2,4 GB en reunión) |
| 7.4 | **Reconocimiento de voz local**: Whisper en WebGPU (WASM si no hay GPU) en vez de Web Speech. Medido en WebView2: `SpeechRecognition` existe pero falla con `network` (no trae el servicio de Google/Microsoft); el reconocimiento de Windows sólo tiene inglés si no se instala un paquete de idioma. Whisper puede dar inglés directo (tarea *translate*) y ahorrar la traducción. | Hecho: Whisper base *translate* |
| 7.5 | **Modelos**: se bajan la primera vez (instalador chico); en la primera reunión, "Preparando la traducción (una sola vez)…" con progreso; quedan en caché. | Hecho (~150 MB con voz de Windows) |

Medido en WebView2 (2026-11-25): WebGPU con adaptador OK, `Translator` existe (falta ver si baja modelo), voces de
Windows sólo salen por parlantes (no sirven para el micrófono virtual). Los modelos son de voz, locales, sin LLM ni
cuenta: el navegador sigue andando completo sin ellos (regla de abajo).

### Fase 8: traductor nativo, sólido, liviano y con prioridad (decidido 2026-11-25)

La fase 7 probó que funciona (1,3 s de demora, Jitsi real) pero como JavaScript en una página oculta: ~2,4 GB de RAM en
reunión, 23 MB de motor web y dependencia del manejo de GPU del motor de páginas. Gonzalo: "algo sólido, no un script,
un apartado diferente, en C si hace falta, bien construido de punta a punta". Orden: 8.1 → 8.2 → 8.3; la calidad de
traducción se ve después.

| # | Qué | Criterio de hecho |
|---|---|---|
| 8.1 | **`astro-voz.exe`**: proyecto C# aparte (`src/Astro.Voz`) sobre whisper.cpp nativo con Whisper.net 1.9.x (runtimes Vulkan para GPU y CPU AVX2 de respaldo), VAD Silero nativo (`WhisperVadFactory`), voz inglesa de Windows (SAPI) adentro. Astro lo lanza al empezar una reunión y lo cierra al terminar; hablan por named pipe en binario (PCM 16 kHz de ida, PCM de vuelta, eventos de estado). Modelos ggml (Whisper base cuantizado + Silero) se bajan la primera vez con progreso. Se borra el motor JS (`voz/`, `src/Navegador/Voz/`, `VozEngine` web). | Misma prueba de punta a punta que la fase 7 (WAV en español como micrófono, página de prueba y meet.jit.si): demora ≤ 1,3 s mediana y **RAM del traductor medida** (meta 200–400 MB). Prueba automática en el repo, no en el scratchpad. |
| 8.2 | **Prioridad a la voz**: proceso del traductor con prioridad alta y hilos de audio en MMCSS ("Pro Audio"); durante una reunión el tope de RAM de Astro cuenta al traductor y descarta antes las pestañas de fondo; la pestaña en reunión y el traductor nunca se tocan. | Prueba con muchas pestañas pesadas abiertas durante la reunión: la demora no sube y no se pierden frases; log con la RAM de cada parte. |
| 8.3 | **Instalador** `AstroSetup.exe` (Inno Setup, LZMA, por usuario, sin admin): instala en `%LOCALAPPDATA%\Programs\Astro`, accesos directos, desinstalar desde Configuración; si falta .NET 8 Desktop Runtime lo baja e instala solo. Nombre visible "Astro". | ~30–40 MB; instalación limpia probada en una carpeta/usuario de prueba; desinstala sin dejar restos. |

**Regla de la fase (Gonzalo, 2026-11-25): el navegador funciona completo sin ningún LLM ni IA.** Las integraciones
(API local, MCP, panel lateral, extensiones) son opcionales y se enchufan desde afuera: nada del navegador las necesita
para andar, ninguna función propia llama a un servicio de IA, y si no hay nada conectado todo sigue igual (el modo
reunión manda tu voz original). El servidor MCP es un programa aparte que sólo usa la API pública, como cualquier otra app.

Cambios a decisiones anteriores: las extensiones dejan de estar fuera de alcance; el canal página→app
(`IsWebMessageEnabled`) se habilita **sólo** en la pestaña con modo reunión y sólo acepta mensajes de audio.
Las pruebas siguen ocultas y con dispositivo de audio falso de Chromium: la reunión real con otra persona la prueba el usuario.

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
Motor propio, sincronización entre equipos, perfiles múltiples, macOS/Linux. (Las extensiones pasaron a la fase 6.)

## Skills y pruebas pendientes (2026-11-12)

Skills instaladas en `~/.agents/skills` para este repo (revisadas antes de instalar; detalle en `~/.omp/agent/AGENTS.md`).
Regla que vale para todo lo de abajo: **ninguna prueba puede mostrar ventanas, robar el foco ni mandar teclas/clics al
escritorio** (`--automation`, `--data-dir` temporal, `tools/compare/cuando_libre.py` para lo pesado).

### Estado de partida
- **Sin commitear desde el 8/11:** `MainWindow.Snapshots.cs` + `MainWindow.Automation.cs` agregan `CapturePreviewAsync` con
  tope de 5 s (la captura no terminaba nunca si la página no dibujaba y colgaba la prueba). Falta: compilar, correr
  `tools/smoke/snapshot.txt` y `uso-real.txt`, entrada en CHANGELOG y commit.
- Herramientas: `wpr.exe` viene con Windows; **faltan** WPA (Windows Performance Toolkit del ADK), `dotnet-counters`,
  `dotnet-trace`, `dotnet-dump` (`dotnet tool install -g …`), Semgrep y CodeQL CLI. Todo gratis.
- Tests: xUnit 2.5.3 en `tests/Navegador.Tests` (Core, Data, Snapshot) con `coverlet.collector` ya referenciado.

### Qué hacer, con qué skill
| # | Qué | Skill | Resultado esperado |
|---|---|---|---|
| 1 | Archivos de `Navegador.Core` sin test propio (`BlockList`, `HttpsUpgrade`, `PageCosts`, `UrlInput`, `InternalPages`, `EventLog`…) | `find-untested-sources` | Lista de huecos |
| 2 | Cobertura real de Core (`dotnet test --collect "XPlat Code Coverage"`) y CRAP de lo más riesgoso (`HibernationPolicy`, `PageCosts`) | `run-tests`, `coverage-analysis`, `crap-score` | Qué métodos complejos no están cubiertos |
| 3 | ¿Los tests atrapan un cambio de comportamiento? Pseudo-mutaciones sobre la política de hibernación, el tope de RAM y HTTPS primero | `test-gap-analysis` | Casos que faltan; escribirlos sólo si atrapan bugs plausibles |
| 4 | Calidad de los tests existentes (aserciones flojas, tiempos, orden) | `test-anti-patterns`, `assertion-quality` | Reporte por severidad |
| 5 | Rendimiento del código propio: `MainWindow.xaml.cs` (1347 líneas), `AppServices`, el filtro de peticiones de `BlockList` (corre en cada petición) | `analyzing-dotnet-performance`; `microbenchmarking` (BenchmarkDotNet) si `BlockList` aparece caliente | Asignaciones y async evitables en la ventana (~95 MB) y el proceso principal |
| 6 | Memoria administrada vs nativa de `Navegador.exe` durante `uso-real.txt` | `dotnet-trace-collect` (`dotnet-counters`, `dotnet-trace`, `dotnet-gcdump`) | Saber si los ~95 MB de la ventana son GC, WPF o nativos |
| 7 | **Piso de GPU de ~200 MB** que no baja nunca (y 450–500 MB con Maps suspendida) | `dotnet-performance` (guía de Windows + GPU: WPR/WPA, ETW) | Explicar de qué es y si `MemoryUsageTargetLevel`/descartes pueden bajarlo |
| 8 | Volcado automático si la app cae (caídas por RTSS, cuelgues) | `dump-collect` | `DOTNET_DbgEnableMiniDump` o WER LocalDumps para `Navegador.exe`, documentado en el README |
| 9 | Configuración peligrosa: `BrowserArguments` editable en `settings.json` (flags arbitrarios a WebView2), `AllowSites`, ventanas nuevas, descargas, `file://` | `sharp-edges` | Lista de "footguns" con arreglo |
| 10 | Análisis estático de C# | `semgrep` (siempre `--metrics=off`) y/o `codeql` (build de la solución) → `sarif-parsing` | Hallazgos de seguridad triados |
| 11 | **Piloto de tests de interfaz** con FlaUI (UIA3): proyecto `tests/Navegador.UiTests`; los `x:Name` de `MainWindow.xaml` (29) ya son AutomationId. Falta un modo que deje la app viva, oculta y sin script (hoy `--automation` corre pasos y se cierra) | `flaui-tests` | 1 test (abrir pestaña, menú ⋮, cerrar) que pase **sin que aparezca nada en pantalla**; si algo se ve, se descarta FlaUI |

### Funciones sin probar de verdad (candidatas para 11 o para `--automation`)
- No dormir la pestaña que suena (`HibernationPolicy`): sólo test unitario; probar con audio en silencio (volumen 0 / pestaña muteada).
- Pantalla completa (F11 y videos).
- Criterios de la fase 4 sin cifra en este archivo: `focus_gained`→`tab_active_ready` p95 < 300 ms y restaurar descartada p95 < 2 s; sacarlos de los logs con `python -m metrics report`.
- Fase 3: todavía no hay logs de uso real con 0.4.x; leerlos cuando existan.
- Fase 5d (ajustes con interfaz, importar favoritos, Ctrl+N, sugerencias en la barra): cada una con su prueba oculta al implementarse.
