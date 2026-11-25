# Muestras de audio para las pruebas

`es-atmolisis.wav` (24,7 s, mono, 16 kHz, 790 KB): una persona leyendo en español el artículo «Atmólisis» de Wikipedia.
Hace de micrófono en `tools/smoke/voz.ps1` (Chromium lo repite solo mientras dura la prueba).

- Origen: Wikipedia grabada, *Es-Atmólisis-article.ogg* en Wikimedia Commons
  (<https://commons.wikimedia.org/wiki/File:Es-Atmólisis-article.ogg>).
- Licencia: la de Wikimedia Commons para ese archivo (CC BY-SA); autoría y licencia exacta en esa página.
- Cambios: convertido a WAV y bajado de 48 kHz a 16 kHz (filtro pasa bajos + diezmado), que es lo que escucha el
  reconocedor. Es la misma grabación con la que se midió la fase 7.

Es lectura enciclopédica, con pocas pausas y palabras raras: sirve para medir demora y memoria, no la calidad de la
traducción. Para afinar la calidad conviene grabar frases de reunión reales y pasarlas con
`voz.ps1 -Wav archivo.wav -Volcar` (deja cada frase con su texto en inglés).
