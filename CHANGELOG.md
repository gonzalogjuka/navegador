# Changelog

## Sin publicar — fase 6.1: modo reunión (micrófono virtual)
**Primer paso hacia "hablo en español y en la reunión me escuchan en inglés".** Botón de micrófono en la barra: en esa pestaña, cuando el sitio pide el micrófono (Meet, Teams web, Zoom web, Jitsi…, con el permiso de siempre) recibe una pista armada por el navegador que mezcla tu voz original y la voz traducida que manda el traductor conectado. Tu voz va al traductor en PCM 16 kHz mono. Todavía no hay forma de conectar un traductor externo: eso es la API local (6.2); por ahora sólo el tono de prueba de las pruebas ocultas.
- Qué escuchan los demás (clic en el botón con el modo activo): automático (traducida si hay traductor), sólo la traducida, traducida con tu voz de fondo, sólo la original. **La reunión nunca queda muda**: sin traductor conectado va la voz original, y la barra de estado avisa en naranja si pediste la traducida.
- Para la página es el micrófono real: mismo nombre, ajustes y silenciar (silenciar en la reunión silencia también lo que va al traductor).
- Activar o desactivar recarga la página: conviene hacerlo antes de entrar a la reunión.
- La pestaña en reunión nunca baja de memoria, ni se duerme ni se descarta (`TabSnapshot.InMeeting`).
- Seguridad: el canal página→app (`IsWebMessageEnabled`) se habilita sólo en la pestaña con modo reunión y se apaga al desactivarlo; del canal sólo se aceptan los mensajes del script, con tope de tamaño.
- Prueba oculta `tools/smoke/reunion.ps1` (micrófono falso de Chromium + `tools/smoke/reunion.html` como sitio de reuniones, que mide lo que oiría la otra persona): 10 de 10 — sin traductor va el micrófono; con el tono de prueba sólo se oye el tono (440 Hz, 100 % de la energía) y la voz llegó al navegador (45 pedazos de ida y 45 de vuelta en ~4 s); "sólo original" y "se desconecta el traductor" vuelven al micrófono; "ambas" mezcla; la página, después de apagar el modo, y otra pestaña no hacen llegar ningún mensaje al navegador. Órdenes nuevas de `--automation`: `meeting on|off`, `meetingvoice`, `translator tone|off`, `meetingstate`, `msgprobe`.
- **Sin probar:** una reunión real con otra persona (Meet, Teams, Zoom); lo prueba el usuario.

## 0.4.2 — 2026-11-08
**El tope de RAM se cumple.** En 0.4.1 el navegador quedaba en ~1,2 GB con tope 1 GB aunque casi todo estuviera dormido. Con el mismo uso simulado (`tools/smoke/uso-real.txt`, 11 pestañas, ~7 min): promedio **1115 → 653 MB**, muestras sobre el tope **71 % → 6 %** (5 de 83, todas mientras carga Maps), en reposo **~1180 → ~490–640 MB**; los mismos 8 despertares, 0 errores.
- **Suspender no alcanza, descartar sí.** Una pestaña suspendida conserva su renderer y su memoria de GPU (Google Maps suspendida: GPU en 450–500 MB; descartada: baja a ~180 en ~10 s). Las 2 pestañas usadas en los últimos 3 min ya no quedan fuera del tope: se descartan al final, cuando no queda otra. Nunca la activa, la que suena ni la que tiene algo escrito.
- **Hace lugar antes de cargar** (pestaña nueva, despertar una dormida, ir a otro sitio): descarta lo necesario para que la página entre, en vez de pasarse y descartar después. Para saber cuánto va a ocupar recuerda lo que sumó la última carga de ese sitio (sólo en memoria, sólo ventana normal; Maps ~550 MB, cuatro veces una página promedio) o usa el promedio de las páginas vivas. Evento `make_room`; `budget_discard` lleva `reason` (`budget` o `room`).
- Al descartar por tope apunta al 85 % del tope, con margen para la próxima página.
- La memoria de páginas incluye el proceso de GPU (nuevo `gpu_private_mb` en las muestras).
- **Límites medidos.** El piso fijo es ~380 MB (ventana ~95, navegador ~50, utilidades ~40 y GPU ~200 tras el primer uso); Maps o YouTube solos llevan el total a ~1050 MB, así que con tope 1 GB se pasa mientras una de esas está activa. Alternando cada 10 s entre Maps, YouTube y La Nación queda una sola viva: cada cambio recarga (9 de 9, 0,36–1,14 s con la foto al instante) y el 59 % de las muestras sigue sobre el tope. Una pestaña descartada tarda ~10 s en devolver la memoria; pedirle memoria baja antes de cerrarla no la acelera (y dejó la GPU en 369 MB en vez de ~180), así que no se usa.

## 0.4.1 — 2026-11-08
- **"Formulario sin enviar" sólo cuenta lo que escribiste vos.** Antes cualquier campo con texto distinto de su valor inicial frenaba el descarte, y eso incluía los cuadros de búsqueda que completa el sitio (Google, Mercado Libre, Maps): en un uso simulado de 11 pestañas, 3 páginas con buscador quedaron protegidas 10 min cada una sin que nadie hubiera escrito nada. Ahora un script en cada página marca los campos que reciben entrada real (`isTrusted`) y los olvida al enviar el formulario; con el mismo uso: 0 `discard_skipped`.
- `--automation`: orden `type TEXTO`, que escribe en el campo enfocado como una persona (CDP `Input.insertText`, eventos de entrada reales). `tools/smoke/formulario.txt` la usa y suma un control: un campo completado por la página se descarta igual. Resultado: la pestaña con texto escrito se salva y el texto sigue ("borrador importante"); la del campo del sitio se descarta.
- `tools/smoke/uso-real.txt`: ~7 min de uso simulado (búsqueda, YouTube, GitHub, diarios, Mercado Libre, Wikipedia, Reddit, Vimeo, Maps), ida y vuelta entre pestañas y ratos quietos.
- Hallazgo del uso simulado (resuelto en 0.4.2): con 11 pestañas el navegador queda en ~1,2 GB con tope 1 GB aunque haya sólo 1 viva, 2 suspendidas y 8 descartadas. Lo que sobra es el proceso de GPU: 200 MB antes de abrir Google Maps y 450–500 MB después, mientras Maps sigue suspendida (y comparte renderer con la búsqueda de Google por el proceso por sitio). El tope no la descarta porque se usó hace menos de 3 min (`BudgetKeepRecentMinutes`).

## 0.4.0 — 2026-11-08
- **Una sola instancia** por carpeta de datos: abrir el navegador otra vez (acceso directo, link de otra app, `.html` desde el Explorador, `--private`) le pasa el pedido a la ventana abierta por un pipe sólo del usuario y termina en ~200 ms, sin ventanas ni log propio. Antes dos procesos escribían la misma sesión y los mismos favoritos.
- **Navegador predeterminado**: menú ⋮ → "Hacer navegador predeterminado…" registra http/https/.htm/.html en HKCU (sin administrador) y abre Configuración para elegirlo; `--register-default` / `--unregister-default`. Los archivos `.html` pasados por línea de comandos se abren como `file:///`.
- **Tope de RAM**:
  - Descarta de una vez las pestañas que hagan falta (estima lo que ocupa cada una con la memoria de los renderers) y espera 15 s a que se libere, salvo que la memoria siga subiendo. Con 9 sitios y tope de 700 MB volvió debajo del tope en un paso (antes: de a una cada 5 s, 8 muestras de 18 sobre el tope; después: 4 de 18 con `BudgetKeepRecentTabs` 0).
  - No descarta las 2 pestañas de fondo que usaste más recientemente si fue en los últimos 3 min (`BudgetKeepRecentTabs`, `BudgetKeepRecentMinutes`). Alternando entre 2 pestañas con tope de 450 MB: antes 14 descartes y 12 recargas (6,2 s recargando) con la misma memoria promedio (528 MB contra 533 MB); ahora 0 y 0. Es lo que pasaba en el uso real del 8/11 (29 descartes por tope y 26 recargas en una hora). Contra: tras abrir muchas pestañas de golpe puede quedar unos minutos por encima del tope.
  - No descarta pestañas con un formulario escrito sin enviar (campos distintos de su valor inicial, editor enfocado con texto, aviso de salida): `discard_skipped`, se reintenta a los 10 min. Una pestaña suspendida no contesta scripts, así que se revisa al suspenderla. Probado con `tools/smoke/formulario.txt`: se descartan las otras, la del texto no, y el texto sigue ahí.
- **CPU en las muestras**: `cpu_pct` y `renderer_cpu_pct` (porcentaje de toda la máquina), y `renderer_private_mb`. Comprobado con `tools/smoke/cpu-suspendida.txt`: una pestaña con un worker en bucle usa 16,5 % (un núcleo de 6) viva o de fondo, 0 % suspendida, y vuelve a 16 % al activarla.
- Una ventana que se abre sin foco (detrás de otra app, o en las pruebas ocultas) empieza a contar el "foco perdido" desde que carga; antes nunca llegaba el aviso de Windows y sus pestañas sólo se dormían por inactividad.
- Si la ventana se cierra mientras saca la foto de la pestaña, la foto se descarta (antes se escribía después de guardar la sesión y quedaba huérfana).
- **Comparación contra Chrome y Edge** (`tools/compare/`): mismas 20 URLs, misma duración, memoria privada de todo el árbol de procesos cada 10 s, con la CPU de la PC al lado para detectar mediciones contaminadas. Chrome for Testing portable en `D:\Tools\chrome-for-testing`. `cuando_libre.py` espera a que la PC esté libre (CPU < 25 %, ≥ 6 GB libres) y corre esta comparación y las pruebas pesadas. **Resultado (fase 4 cumplida):** 20 pestañas, 15 min: Navegador 851 MB promedio, Chrome 3694 MB (0,23), Edge 3533 MB (0,24); sin hibernar 2140 MB (0,58). Las tres pruebas pesadas también dieron OK con la PC libre (rastros privados 4/4, formulario, CPU suspendida 0,0 %).

## 0.3.2 — 2026-11-08
- El perfil privado se borra al salir de la app: se espera a que termine el proceso de WebView2 (0.24 s medidos) y se borra la carpeta, en vez de depender de que WebView2 llegue a hacerlo (en una corrida quedó).
- `--automation` también corre arrancando con `--private` (lo ejecuta la primera ventana que carga); nueva orden `toprivate`. Los pasos y resultados de la prueba van siempre al log de la app.
- `tools/smoke/privada-rastros.ps1`: busca en toda la carpeta de datos (logs, data.json, sesión, snapshots, perfiles de WebView2) una marca visitada en privada, con una corrida de control en la ventana normal que la tiene que encontrar.

## 0.3.1 — 2026-11-08
La ventana privada no trae nada de la normal ni deja actividad.
- No trae: la nueva pestaña privada sólo tiene el buscador (antes mostraba los favoritos); sin estrella, sin menú Favoritos ni Historial, Ctrl+D/Ctrl+H no hacen nada y `about:history` abre la nueva pestaña privada. Usa un perfil de WebView2 propio en modo InPrivate (antes era el InPrivate del perfil normal, que hereda zoom por sitio y permisos): zoom 125 % en la normal → 100 % en la privada; cookie y localStorage de la normal no llegan. Los sitios sin HTTPS detectados y el "permitir este sitio" del escudo son de cada ventana privada, en memoria (antes se compartían, y el escudo escribía el sitio en `settings.json`).
- No deja: las ventanas privadas no escriben nada en el log (antes `navigation_completed`, `https_*`, `restore_tab`… con el host); arrancar con `--private` no crea archivo de log ni reescribe `data.json`. Al cerrar la última ventana privada su perfil se manda a borrar (WebView2 lo borra al terminar) y la siguiente usa uno nuevo; si quedó alguno por un cierre forzado, se borra al arrancar.
- Prueba de humo `tools/smoke/privada.txt`.

## 0.3.0 — 2026-11-08
- **Snapshots**: foto JPEG de la pestaña activa (2 s después de cargar y cada 20 s). Al despertar una pestaña descartada se ve la foto al instante con "Despertando…" y la página real la reemplaza cuando cargó y recuperó el scroll (máximo 8 s de espera). Sobrevive a reinicios.
- El scroll se guarda junto con cada snapshot y en la sesión: también vuelve después de cerrar y abrir el navegador.
- Privacidad: la ventana privada no escribe snapshots; "Borrar datos" los elimina; se borran al cerrar la pestaña y al arrancar si quedaron huérfanos; nombres aleatorios validados.
- Al cambiar de pestaña la barra de direcciones muestra la dirección aunque tenga el foco.

## 0.2.1 — 2026-11-07
- Las pruebas automáticas (`--automation`) ya no molestan: ventanas fuera de pantalla, sin barra de tareas ni foco, menús dibujados sin abrirse (antes Windows los traía al borde visible). Verificado: 163 muestras, 0 veces con el foco, 0 ventanas visibles.
- `--automation` cierra la app siempre al terminar (archivo inexistente, error o sin `close`), para que nunca quede un navegador invisible abierto.
- `tools/rtss-check.ps1` corre oculto. Pasos de prueba guardados en `tools/smoke/`.

## 0.2.0 — 2026-11-07
Controles, seguridad y privacidad.
- Menú ⋮, estrella de favoritos (Ctrl+D), página de nueva pestaña (buscador, favoritos, recientes), historial (`about:history`, Ctrl+H), buscar en la página con barra propia (Ctrl+F), zoom con indicador (Ctrl +/−/0), imprimir (Ctrl+P), descargas (Ctrl+J), menú de pestaña (recargar, duplicar, dormir ahora, cerrar las demás), ventana privada (Ctrl+Shift+N, perfil InPrivate).
- HTTPS primero con vuelta a http si el sitio no tiene HTTPS (también sus subdominios); notificaciones rechazadas; host objects y web messages apagados; contraseñas sin guardar; SmartScreen explícito; candado/advertencia en la barra; borrar datos de navegación (Ctrl+Shift+Supr).
- Páginas propias sin JavaScript, con CSP y texto de sitios escapado (test de inyección incluido).
- Historial y favoritos normalizan la URL (`a.com` = `a.com/`); el historial guarda la dirección final tras redirecciones.
- Los avisos de navegaciones interrumpidas ya no se registran como errores.
- `--automation pasos.txt`: prueba de humo dentro de la app, con capturas que no dependen de la pantalla. Reemplaza las teclas simuladas, que en una prueba se fueron a otra ventana.
- `--private` abre directo una ventana privada.

## 0.1.0 — 2026-11-07
Primera versión usable.
- Repo ordenado: el medidor pasa a `tools/metrics` (20 tests pasan, 1 omitido a propósito en Windows); se borra la copia duplicada `navegador-metrics-v4/`.
- Plan nuevo en `docs/PLAN.md` (WebView2 + .NET 8 en vez de CEF).
- Navegador WPF + WebView2: pestañas, barra de direcciones con búsqueda, atajos, ventanas nuevas como pestañas (conserva `window.opener`), pantalla completa, sesión, reabrir pestaña cerrada.
- Hibernación por inactividad y por foco, presupuesto de RAM, `--process-per-site`, bloqueo de rastreo (Strict + `blocklist.txt` con permiso por sitio).
- Log JSONL compatible con `tools/metrics`; barra de estado con RAM, pestañas dormidas y bloqueos.
- Acceso directo en el escritorio (`tools/install-shortcut.ps1`); distribución de 2.3 MB.
- Recuperación automática si el proceso de WebView2 muere (pestaña restaurada en ~1 s; máximo 3 reinicios en 2 min). Causa observada en esta PC: RivaTuner (RTSSHooks64.dll, 4 minidumps); `tools/rtss-exclude.ps1` crea el perfil de exclusión.
