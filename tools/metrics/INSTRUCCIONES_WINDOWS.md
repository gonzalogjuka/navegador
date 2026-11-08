# Instrucciones: correr las pruebas en Windows

Esta carpeta contiene el módulo de métricas del navegador liviano, listo para correr en tu PC.

## 1. Requisitos (una sola vez)

1. **Python 3.10 o superior.** Desde PowerShell o CMD: `python --version`. Si no está, instalalo desde python.org y marcá "Add python.exe to PATH".
2. **Git for Windows.** Lo necesita la pestaña Code de Claude Desktop. Descargalo de git-scm.com y reiniciá Claude Desktop después de instalarlo.

## 2. Preparar el entorno

Abrí PowerShell dentro de esta carpeta (clic derecho en la carpeta > "Abrir en Terminal") y ejecutá:

```
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
```

Si PowerShell bloquea la activación, ejecutá una vez: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.

Confirmá que el entorno está activo: el prompt debe mostrar `(.venv)` al principio.

## 3. Antes de medir: preparar la línea base

Para que las mediciones con Chrome sean comparables:

1. Cerrá todas las ventanas de Chrome y de Brave que no uses.
2. Abrí Chrome con **20 pestañas** de uso normal (documentación, correo, un video, redes, etc.).
3. Dejá la PC en el estado habitual, sin juegos ni compilaciones corriendo.
4. Cerrá cualquier otra app pesada. Si tenés antivirus en modo "análisis en tiempo real" muy agresivo, anotalo en el informe.
5. Recién después ejecutá las pruebas.

## 4. Ejecutar todo con un comando

Con el entorno activado:

```
python run_windows.py
```

Tarda unos 3 a 4 minutos. Antes de correr, el script verifica Python, dependencias y carpetas. Si algo falta, te dice qué hacer y no ejecuta nada.

Al terminar crea un archivo en `resultados\resultados_AAAAMMDD_HHMMSS.json`.

## 5. Prueba larga (opcional, recomendada)

Para descartar fugas lentas, dejá corriendo 30 minutos mientras usás la PC normalmente:

```
python bench\soak.py --minutes 30 --interval 0.5
```

Guardá la salida JSON junto a los resultados de `run_windows.py`. Podés redirigirla con `> resultados\soak_30min.json`.

## 6. Pasar los resultados al chat de Code

1. Abrí Claude Desktop, pestaña **Code**.
2. En "Select folder", elegí esta carpeta (`navegador-metrics-v4`).
3. Pegá este mensaje:

> Leé INSTRUCCIONES_WINDOWS.md y INFORME_PRUEBAS.md, en especial la sección "Puntos de fallo conocidos". Revisá el archivo más reciente de la carpeta resultados/ y el soak si lo corrí. Compará cada caso contra su criterio, decime qué pasó, qué falló y por qué, y proponé correcciones concretas. Si un fallo viene del sistema operativo, del entorno o de un antivirus y no del código, decímelo aparte y no toques el código por eso.

Si algún comando falla, pegá el error completo en el chat.

## 7. Qué mirar en los resultados

- **Tests:** deben pasar todos, salvo `test_I02`, que se omite en Windows a propósito.
- **P-01b con app:** debe quedar bajo 20 ms.
- **app_private_mb:** tiene que aparecer en el log. Si no aparece, revisá la sección de permisos abajo.
- **foreground:** en la prueba de alt+tab, el nombre del ejecutable debe cambiar.
- **P-03 a 0.1 s:** si la desviación pasa el 10%, puede ser la resolución del reloj de Windows (unos 15 ms), no un error del código.
- **E-01:** si no sube 15 puntos de CPU, puede ser que la PC tenga poca CPU libre o un antivirus que frena los procesos de prueba.
- **Soak:** el crecimiento de RSS debe quedar por debajo de 5 MB en 30 minutos.

## 8. Prueba manual de alt+tab

Mientras corre `soak` o `record`, hacé alt+tab varias veces entre Chrome y otra app. Después revisá en el log que `foreground` haya cambiado:

```
python -m metrics record --out logs\alttab.jsonl --seconds 60 --interval 0.5 --app chrome
python -m metrics report logs\alttab.jsonl
```

## Puntos de fallo conocidos

Estos problemas son previsibles. Si aparecen, no se trata de un error de diseño: hay que tenerlos en cuenta al interpretar los resultados.

| Qué puede pasar | Cómo se ve | Qué hacer |
|---|---|---|
| Un archivo se copió mal | `SyntaxError` o `ImportError` al empezar | Pegá el error completo en Code; suele ser un carácter mal copiado |
| Python viejo o venv sin activar | `run_windows.py` avisa en el preflight | Seguir la sección 2 |
| Caracteres especiales en la salida | Ya está cubierto con `encoding="utf-8"` | Si igual aparece, avisá |
| Permisos de memoria | `app_private_mb` no aparece en el log | Probar la terminal como administrador una vez y anotar el resultado |
| Procesos `python.exe` de otras herramientas | Conteos de procesos distintos a lo esperado | Anotar cuántos había abiertos; no invalida las pruebas de cambio |
| Antivirus o Defender | Pruebas lentas, timeouts, o E-01 sin subida de CPU | Anotar la configuración; probar con el antivirus en pausa solo para la prueba si es seguro hacerlo |
| Resolución del reloj (~15 ms) | P-03 a 0.1 s con desvío mayor al 10% | Registrar la cifra; es una limitación del sistema |
| Ventanas de otras apps que cambian el foco | `foreground` con valores inesperados | Hacer la prueba de alt+tab sin tocar nada más |
| Timeouts de 60 a 600 s | El script corta una prueba | Revisar si la PC está muy cargada y repetir |

## Problemas frecuentes

- **"python no se reconoce":** reinstalá Python con la opción PATH marcada, o usá `py` en lugar de `python`.
- **pytest no encuentra módulos:** estar en la carpeta del proyecto con el entorno activado.
- **`pip` instala en otro Python:** usá `python -m pip install -r requirements.txt` para asegurarte de que sea el mismo intérprete.
- **Los tests de integración tardan:** es normal, lanzan subprocesos y esperan a que terminen.
