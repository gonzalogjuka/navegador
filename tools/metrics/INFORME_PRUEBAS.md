# Informe de pruebas: navegador-metrics

Entorno de ejecución original: Linux (contenedor), Python 3.12, psutil 7.2.2.
Suite: 21 tests, todos pasan en Linux. Escenarios E-01 a E-04 y soak de 4 minutos, todos OK.
No se ejecutó todavía en Windows ni con CEF. Los resultados de Windows se agregan en la sección "Resultados en Windows".

## Resultados por fase (Linux)

| Fase | Casos | Resultado |
|------|-------|-----------|
| 1. Lógica y casos borde | L-01 a L-09 | 9/9 pasan (tras una corrección, ver H-1) |
| 2. Integración | I-01 a I-03 | 3/3 pasan |
| 3. Costo del medidor | P-01 a P-03 | 3/3 criterios cumplidos |
| 4. Escenarios y larga duración | E-01 a E-04, soak | 4/4 y soak OK |

## Hallazgos y correcciones

**H-1 (corregido): span corrupto rompía el resumen.** Un registro `span` sin `duration_ms`
lanzaba `KeyError` en `span_summary`. Ahora se descarta ese registro. Lo detectó L-03.

**H-2 (mejora aplicada): recorrer todos los procesos en cada muestra.** Con filtro de app,
`collect()` costaba 2.5 ms por muestra. Ahora la lista se refresca cada 2 s. El costo bajó a 0.43 ms.

**H-3 (mejora aplicada): solo se medía RSS.** Ahora se registra también `app_private_mb`.
Si el sistema no lo permite, el campo no aparece.

**H-4 (corregido para Windows): prueba I-02 con SIGINT.** `send_signal(SIGINT)` no funciona igual
en Windows. Esa prueba se omite en Windows; el Ctrl+C se verifica a mano.

## Mediciones (Linux)

| Caso | Valor | Criterio | Estado |
|------|-------|----------|--------|
| P-01 `collect()` sin filtro de app | 0.24 ms promedio, 2.7 ms máximo | < 20 ms | OK |
| P-01b con filtro, sin caché (versión anterior) | 2.5 ms promedio, 5.5 ms máximo | < 20 ms | OK |
| P-01b con filtro, con caché (versión actual) | 0.43 ms promedio, 3.7 ms máximo | < 20 ms | OK |
| P-02 crecimiento de RSS en 60 s a 0.1 s | +0.11 MB | < 5 MB | OK |
| P-03 muestras a 0.1 s | 99 de 100 (1.0%) | < 10% | OK |
| P-03 muestras a 0.5 s | 20 de 20 (0%) | < 10% | OK |
| P-03 muestras a 1.0 s | 10 de 10 (0%) | < 10% | OK |
| E-01 carga real de CPU | 0.9% sin carga, 100% con carga | subida > 15 puntos | OK |
| E-02 proceso que aparece y desaparece | 1 → 2 → 1 | conteo correcto | OK |
| E-03 latencia de `stop()` con intervalo de 5 s | 0.2 ms | < 500 ms | OK |
| E-04 reporte de 100 000 líneas (8.7 MB) | 0.39 s | < 5 s | OK |
| Soak 4 min a 0.1 s | RSS 15.63 → 15.88 MB (+0.25 MB), 2360 muestras | crecimiento < 5 MB | OK |

**Tamaño del log.** A 0.1 s el log crece unos 9.3 MB por hora. A 0.5 s, el valor por defecto,
sería cerca de una quinta parte.

## Lo que estas pruebas no demuestran (pendiente en Windows)

- **Chrome real con 20 pestañas.** El costo del filtro puede crecer con 300 o más procesos.
- **Foco en primer plano.** `GetForegroundWindow` nunca se ejecutó. En Linux el campo queda en `None`.
- **Resolución del temporizador de Windows.** Ronda los 15 ms; el intervalo de 0.1 s puede desviarse más.
- **RSS vs memoria privada.** Confirmar que `app_private_mb` aparece con permisos normales.
- **Fugas lentas.** Se necesita un soak de 30 minutos o más en Windows.
- **Puntos de fallo del entorno Windows.** Copia de archivos, versión de Python, permisos de memoria, antivirus, resolución del reloj y procesos ajenos. Están listados en INSTRUCCIONES_WINDOWS.md, sección "Puntos de fallo conocidos". Al analizar resultados, distinguir fallos de código de fallos del entorno.

## Resultados en Windows

(Completar con los JSON de `resultados\` y el soak.)

| Caso | Valor | Criterio | Estado |
|------|-------|----------|--------|
|      |       |          |        |

## Estado

El módulo pasa todas las pruebas que se pueden ejecutar en Linux. Queda como "línea base válida"
cuando se completen los casos de la sección de Windows.
