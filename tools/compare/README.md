# Comparación de memoria contra Chrome y Edge (fase 4)

`compare.py` abre las mismas URLs (`urls.txt`, 20 sitios comunes sin login ni audio/video automático) en cada
navegador, una pestaña cada 3 s, las deja quietas la misma cantidad de minutos y mide cada 10 s la memoria privada
(`private bytes`) sumada de todo el árbol de procesos. Criterio del PLAN: promedio de Navegador ≤ 50 % del de Chrome.

```powershell
tools\metrics\.venv\Scripts\python.exe tools\compare\compare.py            # 20 pestañas, 15 min, 4 escenarios
tools\metrics\.venv\Scripts\python.exe tools\compare\compare.py --minutes 2 --urls 5   # prueba corta
```

Opciones: `--minutes` (default 15), `--scenarios navegador,navegador-sin-hibernar,chrome,edge`,
`--exe` (default `dist\Navegador.exe`), `--urls N` (las primeras N de `urls.txt`), `--esperar-pc-libre` (antes de
cada escenario espera 3 controles seguidos, uno por minuto, con CPU de la PC < 25 % y ≥ 6 GB de RAM disponible).

## Correr todo cuando la PC esté libre

`cuando_libre.py` espera a que la PC esté libre y corre las pruebas pesadas: rastros de la ventana privada
(`tools/smoke/privada-rastros.ps1`), formulario con tope de RAM, CPU de una pestaña suspendida y esta comparación.
Deja todo en `resultados/<fecha>-libre.md`. Para dejarlo esperando sin ventana:

```powershell
# desde la raíz del repo
Start-Process -WindowStyle Hidden -FilePath tools\metrics\.venv\Scripts\python.exe `
  -ArgumentList 'tools\compare\cuando_libre.py' -RedirectStandardOutput tools\compare\resultados\cuando_libre.log
```

## Escenarios

Corren uno detrás del otro, cada uno con su carpeta temporal, que se borra al final.

- `navegador`: `Navegador.exe --data-dir <tmp> --automation <pasos>` con los ajustes por defecto (hibernación activa).
  Los pasos son `go`/`newtab URL` + `wait 3000`, después `wait <minutos>` y `close`.
- `navegador-sin-hibernar`: igual, pero con un `settings.json` que apaga suspensión, descarte, foco perdido y
  presupuesto de RAM (todo en 999999). Separa lo que ahorra el shell de lo que gasta el motor pelado.
- `chrome`: Chrome for Testing (portable, en `D:\Tools\chrome-for-testing\chrome-win64`, bajado del endpoint oficial
  `last-known-good-versions-with-downloads.json`) con `--headless=new`, `--user-data-dir` temporal,
  `--disable-sync` y `--disable-extensions`. La primera URL va en la línea de comandos y el resto se abre por CDP
  (`/json/new`) con la misma pausa de 3 s.
- `edge`: lo mismo con el `msedge.exe` instalado. Sin `--disable-sync`/`--disable-extensions`, Edge con un perfil
  nuevo inicia sesión solo con la cuenta de Windows y baja las extensiones del usuario (se vieron AdBlock y otra),
  que abren pestañas propias y suben su memoria.

Nunca aparece una ventana: Navegador corre en modo automatización (ventanas fuera de pantalla, sin barra de tareas,
sin foco) y Chrome/Edge sin cabeza.

## Qué mide

Árbol de procesos = el proceso lanzado + todo proceso cuya línea de comandos contiene la carpeta temporal + sus
descendientes. Se suma `memory_info().private` de psutil; los procesos que terminan en medio de la muestra se saltean.
Al terminar (o ante un error) se matan los restos del escenario por esa misma coincidencia. Cada muestra guarda también
los MB por tipo de proceso (`host` = Navegador.exe, `browser`, `renderer`, `gpu-process`, `utility`...) y la CPU de
toda la PC (`system_cpu_pct`; la tabla muestra el promedio: si es alta, otra app estaba compitiendo).

Salida en `resultados/`: un JSON por escenario (`<fecha>-<escenario>.json`, con todas las muestras y el resumen
prom./p50/p95/máx./final y procesos promedio) y `<fecha>-comparacion.md` con la tabla, las proporciones
navegador/chrome y navegador/edge y, para los escenarios de Navegador, cuántos `discard_tab`, `budget_discard`,
`restore_tab`, `suspend_tab` y `automation_error` hubo en su log.

## Cuidado al leer los números

- Asimetría: Chrome/Edge corren sin cabeza (no dibujan ventana ni GPU de composición visible); Navegador tiene
  ventanas WPF reales fuera de pantalla. Eso juega en contra de Navegador.
- Las pestañas de Navegador quedan en una ventana que nunca tiene foco: la hibernación arranca en cuanto vencen sus
  plazos, como en una ventana minimizada.
- Navegador trae su bloqueador de publicidad prendido (ajuste por defecto) y Chrome/Edge corren sin extensiones:
  parte de la diferencia es publicidad que Navegador no carga.
- Los sitios cambian de un día al otro (portadas de diarios, publicidad): comparar escenarios de la misma corrida.
