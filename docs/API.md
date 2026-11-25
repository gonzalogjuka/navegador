# API local del navegador

Para apps de tu PC que quieren trabajar con el navegador: un traductor simultáneo, un asistente con Claude u otro LLM,
herramientas propias. Versión 1 (fases 6.2 y 6.3): **audio del modo reunión**, **pestañas y texto de las páginas**, y un
**servidor MCP** listo para usar. Todo es opcional: el navegador funciona completo sin ninguna app conectada.

## Conexión

- **Dirección:** `ws://localhost:47800/v1` (WebSocket). El puerto se cambia con `ApiPort` en `settings.json`. Mientras
  el navegador está abierto, la dirección real queda en `%LOCALAPPDATA%\Navegador\api.json`:
  `{"url":"ws://localhost:47800/v1","port":47800,"pid":1234}`. Leé ese archivo en vez de fijar el puerto.
- **Sólo desde la PC:** escucha en `127.0.0.1` y `::1`. Se rechaza (403) todo pedido con cabecera `Origin`: una página
  web nunca puede usar la API. También se rechaza un `Host` que no sea `localhost`, `127.0.0.1` o `[::1]`.
- Apagar la API: `ApiEnabled: false` en `settings.json`.

## Permiso (una vez por app)

El primer mensaje tiene que ser `hello`, en texto JSON, dentro de los 10 s:

```json
{"type": "hello", "app": "Mi traductor", "token": null, "wants": ["meeting.audio"]}
```

- **Sin token (o con uno revocado):** el navegador muestra una barra: *«Mi traductor», una app de esta PC, quiere
  conectarse al navegador para: …* con **Permitir / Rechazar**. Si nadie contesta en 2 minutos, cuenta como rechazo.
  - Permitido: `{"type":"welcome","app":"Mi traductor","scopes":["meeting.audio"],"token":"…"}`. **Guardá el token**:
    es la única vez que se manda (el navegador guarda sólo su hash).
  - Rechazado: `{"type":"denied"}` y se cierra la conexión.
- **Con token válido:** `{"type":"welcome","app":"…","scopes":[…]}` sin preguntar.
- Menú ⋮ → **Olvidar apps conectadas** borra todos los permisos y corta las conexiones abiertas.

| Permiso | Qué deja hacer |
|---|---|
| `meeting.audio` | Recibir tu voz de las pestañas con modo reunión y mandar la voz traducida, que es lo que escucha la reunión. |
| `tabs.read` | Ver las pestañas (título, dirección, estado) y leer el texto visible de las páginas. |
| `tabs.control` | Abrir sitios web (sólo `http`/`https`) y cambiar de pestaña. |
| `meeting.control` | Activar o desactivar el modo reunión en una pestaña. |

Las **ventanas privadas no existen para la API**: no se listan, no se leen y no se controlan.

Los permisos desconocidos se ignoran. Si una app no pide ninguno conocido, se rechaza.

## Modo reunión: audio

Con `meeting.audio`, la app pasa a ser **el traductor** (uno a la vez: si se conecta otro, reemplaza al anterior).
Mientras está conectada, en las pestañas con modo reunión la otra persona escucha lo que manda la app (o la mezcla que
eligió el usuario en el botón del micrófono). Si la app se desconecta, la reunión vuelve sola a tu voz original:
**nunca queda muda**.

**Paquetes binarios** (mensaje WebSocket binario, todo little-endian):

| Byte | Campo |
|---|---|
| 0 | Tipo: `1` = tu voz (navegador → app), `2` = voz para la reunión (app → navegador) |
| 1–4 | Pestaña (`int32`), la de `meetings` |
| 5–8 | Frecuencia de muestreo (`int32`): siempre 16000 en tipo 1; entre 8000 y 48000 en tipo 2 |
| 9… | PCM mono, 16 bits con signo |

- **Tu voz (tipo 1):** llega en pedazos de ~21 ms (unos 340 muestras), sólo mientras la pestaña tiene el micrófono
  abierto, no está silenciada en la reunión y la mezcla usa la traducción. Ya viene con cancelación de eco y supresión
  de ruido de Chromium.
- **Voz traducida (tipo 2):** mandala en pedazos chicos apenas los tengas (20–100 ms es ideal), a la frecuencia que
  produzca tu síntesis de voz (por ejemplo 24000): el navegador remuestrea. Se reproduce en orden; si llega más de 1 s
  atrasada se descarta el pedazo para no acumular demora.

**Mensajes de texto que manda el navegador:**

```json
{"type": "meetings", "tabs": [{"id": 3, "title": "Meet - Reunión", "host": "meet.google.com", "mic": true, "voice": "Auto"}]}
```

Llega al conectarse y cada vez que cambia algo: una pestaña entra o sale del modo reunión, la página abre o cierra el
micrófono, el usuario cambia la mezcla (`Auto`, `Original`, `Translated`, `Both`).

**Mantener viva la conexión:** `{"type":"ping"}` → `{"type":"pong"}` (opcional: el navegador también manda pings de
WebSocket cada 20 s).

## Pedidos (pestañas, páginas, modo reunión)

```json
{"type": "call", "id": 1, "method": "tabs.list", "params": {}}
{"type": "result", "id": 1, "result": [{"id": 3, "title": "…", "url": "https://…", "active": true, "state": "Live", "meeting": false}]}
{"type": "result", "id": 2, "error": "falta el permiso tabs.control"}
```

| Método | Permiso | Parámetros | Resultado |
|---|---|---|---|
| `tabs.list` | `tabs.read` | — | pestañas de las ventanas normales |
| `page.text` | `tabs.read` | `tab` (opcional: la activa) | `{tab, title, url, text, truncated}`; hasta 100 000 caracteres. Una pestaña dormida da error: la API no la despierta (no gasta RAM a escondidas) |
| `tabs.open` | `tabs.control` | `url` (`http`/`https`) | `{tab}`: pestaña nueva, activa |
| `tabs.activate` | `tabs.control` | `tab` | `{tab}` (si estaba dormida, se despierta como con un clic) |
| `meeting.set` | `meeting.control` | `on`, `tab` (opcional) | `{tab, meeting}`; recarga la página |

## Servidor MCP (Claude u otro LLM)

`navegador-mcp.exe` (proyecto [`src/Navegador.Mcp`](../src/Navegador.Mcp/Program.cs), sin dependencias) es un servidor
MCP por stdio: un programa aparte que usa esta misma API con su propio permiso. Herramientas: `list_tabs`, `read_page`,
`open_url`, `activate_tab`, `set_meeting_mode`. La primera vez el navegador pregunta «navegador-mcp quiere
conectarse…»; el token queda en `%LOCALAPPDATA%\NavegadorMcp\token.txt`.

```text
dotnet publish src/Navegador.Mcp -c Release -o dist-mcp
```

Configuración del cliente (Claude Desktop: `claude_desktop_config.json`; Claude Code: `claude mcp add navegador -- <ruta>`):

```json
{"mcpServers": {"navegador": {"command": "C:\\ruta\\dist-mcp\\navegador-mcp.exe"}}}
```

## Demora

Lo que agrega el navegador (medido con `tools/smoke/integracion.ps1`): ~21 ms del pedazo de voz + 1–9 ms de ida y vuelta
por la API + 40 ms de colchón antes de reproducir (sube solo hasta 150 ms si la página está cargada y hay cortes).
**Casi toda la demora la pone el traductor.** Para que sea lo más rápido posible:

- reconocimiento de voz **en streaming** (resultados parciales), no por frase completa;
- traducción incremental y voz sintética en streaming (mandar el primer pedazo de audio apenas sale);
- voz sintética estándar: clonar la voz agrega demora.

## Cliente de ejemplo

[`tools/integraciones/Eco`](../tools/integraciones/Eco/Program.cs) (C#, sin dependencias) hace todo el recorrido:
lee `api.json`, manda `hello`, guarda el token y contesta cada pedazo de voz con un tono (`--tone`) o con la misma voz
(`--echo`, para medir la demora en una reunión real).

```text
dotnet run --project tools/integraciones/Eco -- --echo
```

Esbozo en Python (**sin probar**; requiere `pip install websockets`):

```python
import asyncio, json, os, struct, websockets

async def main():
    info = json.load(open(os.path.expandvars(r"%LOCALAPPDATA%\Navegador\api.json")))
    token = open("token.txt").read().strip() if os.path.exists("token.txt") else None
    async with websockets.connect(info["url"]) as ws:
        await ws.send(json.dumps({"type": "hello", "app": "Mi traductor", "token": token, "wants": ["meeting.audio"]}))
        async for msg in ws:
            if isinstance(msg, str):
                m = json.loads(msg)
                if m["type"] == "welcome" and "token" in m:
                    open("token.txt", "w").write(m["token"])
                continue
            kind, tab, rate = struct.unpack_from("<Bii", msg)
            if kind != 1:
                continue
            pcm = msg[9:]                      # tu voz: PCM 16 kHz mono 16 bits
            translated, out_rate = pcm, 16000  # acá va la traducción (STT -> traducción -> TTS)
            await ws.send(struct.pack("<Bii", 2, tab, out_rate) + translated)

asyncio.run(main())
```

## Pruebas

- `tools/smoke/integracion.ps1`: navegador oculto + Eco real. Ataques (Origin, Host ajeno), aprobar, volver con
  token, la app se va, olvidar apps, rechazar. Termina en `RESULTADO: OK` o `FALLA`.
- `tools/smoke/reunion.ps1`: el micrófono virtual con el traductor de prueba interno.
- `tools/smoke/mcp.ps1`: `navegador-mcp.exe` hablado por stdio (sin ningún LLM) contra un navegador oculto con una
  ventana privada abierta: permiso, listar (sin la privada), leer, abrir (rechaza `file:`, `javascript:`, `data:`),
  modo reunión, cambiar de pestaña.
