# Changelog

## 0.1.0 — 2026-11-07
Primera versión usable.
- Repo ordenado: el medidor pasa a `tools/metrics` (20 tests pasan, 1 omitido a propósito en Windows); se borra la copia duplicada `navegador-metrics-v4/`.
- Plan nuevo en `docs/PLAN.md` (WebView2 + .NET 8 en vez de CEF).
- Navegador WPF + WebView2: pestañas, barra de direcciones con búsqueda, atajos, ventanas nuevas como pestañas (conserva `window.opener`), pantalla completa, sesión, reabrir pestaña cerrada.
- Hibernación por inactividad y por foco, presupuesto de RAM, `--process-per-site`, bloqueo de rastreo (Strict + `blocklist.txt` con permiso por sitio).
- Log JSONL compatible con `tools/metrics`; barra de estado con RAM, pestañas dormidas y bloqueos.
- Acceso directo en el escritorio (`tools/install-shortcut.ps1`); distribución de 2.3 MB.
- Recuperación automática si el proceso de WebView2 muere (pestaña restaurada en ~1 s; máximo 3 reinicios en 2 min). Causa observada en esta PC: RivaTuner (RTSSHooks64.dll, 4 minidumps); `tools/rtss-exclude.ps1` crea el perfil de exclusión.
