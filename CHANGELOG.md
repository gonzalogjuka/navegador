# Changelog

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
