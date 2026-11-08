# Notas de sesión, 2026-11-06 (para seguir en otra máquina)

Máquina de esta sesión: Windows 11 (10.0.26300), Python 3.15.2, psutil 7.2.2, pytest 9.2.0.
Código del proyecto: SIN modificar. Solo se creó `.venv\` y `resultados\resultados_20261106_175003.json`.

## Qué se hizo
1. `python -m venv .venv` + `pip install -r requirements.txt`.
2. `.venv\Scripts\python.exe run_windows.py` (tarda ~2 min). Resultado en `resultados\resultados_20261106_175003.json`.
3. Se rehicieron los tests con otra carpeta temporal y se verificó E-02 a mano.

## Resultados

| Caso | Valor | Criterio | Estado |
|---|---|---|---|
| Tests (corrida del runner) | 13 pasan, 1 omitido, 7 ERROR | todos | ERROR de entorno (ver abajo) |
| Tests con `--basetemp` alterno | 20 pasan, 1 omitido (`test_I02`, a propósito en Windows) | todos | OK |
| P-01 sin app | 6.7 ms prom, 15.5 ms máx | < 20 ms | OK |
| P-01b con app | 8.5 ms prom, 21.6 ms máx | < 20 ms | OK en promedio (Linux: 0.43 ms) |
| P-02 RSS 60 s a 0.1 s | +0.07 MB | < 5 MB | OK |
| P-03 a 0.1 s | 83 de 100, desvío 17% | < 10% | FALLA (probable reloj de Windows, no verificado) |
| P-03 a 0.5 s / 1.0 s | 0% / 0% | < 10% | OK |
| E-01 CPU | 8.4% -> 65.8% | subida > 15 pts | OK |
| E-02 aparece/desaparece | 4 -> 6 -> 4 | antes+1, luego antes | FALLA (artefacto del entorno) |
| E-03 stop() | 0.6 ms | < 500 ms | OK |
| E-04 reporte 100k líneas | 0.29 s (8.8 MB) | < 5 s | OK |

## Fallos y por qué

- **7 tests con error en la corrida del runner:** `PermissionError [WinError 5]` al leer
  `C:\Users\GonzaloGjuka\AppData\Local\Temp\pytest-of-GonzaloGjuka`. Es el sistema, no el código.
  Con `python -m pytest -q tests/ --basetemp=<carpeta propia> -p no:cacheprovider` pasan los 20.
  `run_windows.py` no usa `--basetemp`, así que en esa máquina sigue dando error.
- **E-02:** comprobado a mano. El `python.exe` del venv en Windows es un lanzador que abre un segundo
  proceso (`C:\Python315\python.exe`), entonces cada `Popen` crea 2 procesos. El caché cuenta bien
  (sube de a 2 y vuelve a 4). El criterio `antes + 1` de `bench/escenarios.py` no es válido con venv en Windows.
  Opciones, NO aplicadas: criterio `antes + 1` o `antes + 2`, o lanzar el hijo con el Python base.
- **P-03 a 0.1 s (17%):** hipótesis: resolución del reloj de Windows (~15 ms). No se midió el reloj.
- **P-01 ~28 veces más lento que en Linux (0.24 ms):** sin explicación comprobada. Esta corrida se hizo
  SIN la línea base preparada (sin 20 pestañas de Chrome, sin cerrar apps, antivirus sin anotar).

## Pendiente
- Preparar la línea base (sección 3 de `INSTRUCCIONES_WINDOWS.md`) y repetir `run_windows.py`; comparar P-01 y P-03.
- Soak de 30 min: `python bench\soak.py --minutes 30 --interval 0.5 > resultados\soak_30min.json` (criterio: RSS < 5 MB).
- Prueba manual de alt+tab (sección 8): confirmar que `foreground` cambia; confirmar que `app_private_mb` aparece en el log.
- Anotar antivirus/Defender de la máquina.
- Decidir (Gonzalo) si se ajusta el criterio de E-02 para Windows y si `run_windows.py` usa `--basetemp`.
- Completar la tabla "Resultados en Windows" de `INFORME_PRUEBAS.md` (sigue vacía).

## Para retomar en la otra máquina
Copiar esta carpeta completa (sin `.venv`, se recrea), seguir `INSTRUCCIONES_WINDOWS.md` secciones 1 a 4
y pegar en Code el mensaje de la sección 6 más estas notas.
