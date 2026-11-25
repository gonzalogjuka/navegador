// navegador-mcp: servidor MCP (stdio, JSON-RPC 2.0 por línea) que le da a un LLM herramientas para el navegador.
// Opcional: el navegador anda igual sin esto. Usa sólo la API local pública (docs/API.md), con su propio permiso: la
// primera vez el navegador muestra "«navegador-mcp» quiere conectarse…" y la persona decide.
//   navegador-mcp [--data-dir <carpeta del navegador>] [--token-file <archivo>]
// Ejemplo para Claude Desktop / Claude Code (mcpServers): {"navegador": {"command": "C:\\...\\navegador-mcp.exe"}}
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var link = new BrowserLink(
    Arg("--data-dir") ?? Path.Combine(local, "Navegador"),
    Arg("--token-file") ?? Path.Combine(local, "NavegadorMcp", "token.txt"));
_ = link.EnsureConnectedAsync(); // pide el permiso enseguida, no recién con la primera herramienta

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
var writeLock = new SemaphoreSlim(1, 1);

async Task Write(JsonNode message)
{
    await writeLock.WaitAsync();
    try { await stdout.WriteLineAsync(message.ToJsonString()); }
    finally { writeLock.Release(); }
}

while (await stdin.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonNode? msg;
    try { msg = JsonNode.Parse(line); }
    catch (JsonException) { await Write(Mcp.Error(null, -32700, "JSON inválido")); continue; }
    var id = msg?["id"]?.DeepClone();
    var method = msg?["method"]?.GetValue<string>();
    if (method is null || id is null) continue; // notificaciones (notifications/initialized…): sin respuesta
    _ = Task.Run(async () => await Write(await Mcp.HandleAsync(link, id, method, msg!["params"])));
}

static class Mcp
{
    private const string Protocol = "2025-06-18";

    private static readonly (string Name, string Description, JsonObject Schema)[] Tools =
    [
        ("list_tabs", "Lista las pestañas abiertas del navegador (id, título, dirección, si está activa, dormida o en modo reunión). No incluye ventanas privadas.",
            Schema()),
        ("read_page", "Devuelve el texto visible de una página (por defecto, la pestaña activa). Una pestaña dormida hay que activarla primero con activate_tab.",
            Schema(("tab", "integer", "id de la pestaña (de list_tabs); vacío = la activa", false))),
        ("open_url", "Abre un sitio web (http o https) en una pestaña nueva y la activa.",
            Schema(("url", "string", "dirección http:// o https://", true))),
        ("activate_tab", "Pone una pestaña al frente (si estaba dormida, se despierta).",
            Schema(("tab", "integer", "id de la pestaña (de list_tabs)", true))),
        ("set_meeting_mode", "Activa o desactiva el modo reunión (micrófono virtual para traducir la voz) en una pestaña. Recarga la página: conviene antes de entrar a la reunión.",
            Schema(("on", "boolean", "true para activar, false para desactivar", true), ("tab", "integer", "id de la pestaña; vacío = la activa", false))),
    ];

    public static async Task<JsonNode> HandleAsync(BrowserLink link, JsonNode id, string method, JsonNode? p)
    {
        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = p?["protocolVersion"]?.GetValue<string>() ?? Protocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "navegador", ["version"] = "0.1.0" },
                    ["instructions"] = "Herramientas para el navegador Navegador de esta PC. Sólo ve ventanas normales, nunca las privadas.",
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(Tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone(),
                    }).ToArray()),
                });
            case "tools/call":
                var name = p?["name"]?.GetValue<string>() ?? "";
                var a = p?["arguments"] as JsonObject ?? [];
                var (text, isError) = await CallToolAsync(link, name, a);
                return Result(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError,
                });
            default:
                return Error(id, -32601, $"método desconocido: {method}");
        }
    }

    private static async Task<(string Text, bool IsError)> CallToolAsync(BrowserLink link, string tool, JsonObject a)
    {
        var (method, prms) = tool switch
        {
            "list_tabs" => ("tabs.list", new JsonObject()),
            "read_page" => ("page.text", Pick(a, "tab")),
            "open_url" => ("tabs.open", Pick(a, "url")),
            "activate_tab" => ("tabs.activate", Pick(a, "tab")),
            "set_meeting_mode" => ("meeting.set", Pick(a, "tab", "on")),
            _ => ("", new JsonObject()),
        };
        if (method == "") return ($"herramienta desconocida: {tool}", true);
        try
        {
            var result = await link.CallAsync(method, prms);
            if (method == "page.text" && result is JsonObject page)
                return ($"{page["title"]}\n{page["url"]}\n\n{page["text"]}" + (page["truncated"]?.GetValue<bool>() == true ? "\n\n[texto recortado]" : ""), false);
            return (result?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "ok", false);
        }
        catch (BrowserException ex)
        {
            return (ex.Message, true);
        }
    }

    private static JsonObject Pick(JsonObject a, params string[] keys)
    {
        var o = new JsonObject();
        foreach (var k in keys) if (a[k] is { } v) o[k] = v.DeepClone();
        return o;
    }

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var (n, t, d, _) in props) properties[n] = new JsonObject { ["type"] = t, ["description"] = d };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(props.Where(x => x.Required).Select(x => (JsonNode)x.Name).ToArray()),
        };
    }

    private static JsonObject Result(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

sealed class BrowserException(string message) : Exception(message);

/// <summary>Conexión con la API local del navegador; se rehace sola si el navegador se cerró y volvió.</summary>
sealed class BrowserLink(string dataDir, string tokenFile)
{
    private static readonly string[] Wants = ["tabs.read", "tabs.control", "meeting.control"];
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonNode>> _pending = [];
    private ClientWebSocket? _ws;
    private Task<string?>? _welcome;
    private long _nextId;

    public async Task EnsureConnectedAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            if (_ws?.State == WebSocketState.Open && _welcome is { IsCompleted: true } done && await done is null) return;
            var info = Path.Combine(dataDir, "api.json");
            if (!File.Exists(info)) throw new BrowserException("El navegador no está abierto (o tiene la API apagada en los ajustes).");
            var url = JsonNode.Parse(await File.ReadAllTextAsync(info))!["url"]!.GetValue<string>();
            _ws?.Dispose();
            var ws = _ws = new ClientWebSocket();
            try { await ws.ConnectAsync(new Uri(url), CancellationToken.None); }
            catch (WebSocketException) { throw new BrowserException("El navegador no está abierto (o tiene la API apagada en los ajustes)."); }
            var welcome = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _welcome = welcome.Task;
            _ = ReceiveLoopAsync(ws, welcome);
            var token = File.Exists(tokenFile) ? (await File.ReadAllTextAsync(tokenFile)).Trim() : null;
            await SendAsync(ws, new JsonObject { ["type"] = "hello", ["app"] = "navegador-mcp", ["token"] = token, ["wants"] = new JsonArray(Wants.Select(w => (JsonNode)w).ToArray()) });
            Console.Error.WriteLine("navegador-mcp: esperando el permiso en el navegador…");
            if (await welcome.Task is { } problem) throw new BrowserException(problem);
        }
        finally { _connectLock.Release(); }
    }

    public async Task<JsonNode?> CallAsync(string method, JsonObject prms)
    {
        await EnsureConnectedAsync();
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        await SendAsync(_ws!, new JsonObject { ["type"] = "call", ["id"] = id, ["method"] = method, ["params"] = prms });
        JsonNode reply;
        try { reply = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            lock (_pending) _pending.Remove(id);
            throw new BrowserException("El navegador no contestó en 30 s.");
        }
        if (reply["error"] is { } err) throw new BrowserException(err.GetValue<string>());
        return reply["result"];
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, TaskCompletionSource<string?> welcome)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType != WebSocketMessageType.Text) continue;
                var m = JsonNode.Parse(ms.ToArray());
                switch (m?["type"]?.GetValue<string>())
                {
                    case "welcome":
                        if (m["token"] is { } t)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
                            await File.WriteAllTextAsync(tokenFile, t.GetValue<string>());
                        }
                        welcome.TrySetResult(null);
                        break;
                    case "denied":
                        welcome.TrySetResult("La persona no le dio permiso a navegador-mcp en el navegador.");
                        break;
                    case "result":
                        var id = m["id"]!.GetValue<long>();
                        TaskCompletionSource<JsonNode>? tcs;
                        lock (_pending) { _pending.Remove(id, out tcs); }
                        tcs?.TrySetResult(m);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or JsonException or IOException) { }
        finally
        {
            welcome.TrySetResult("Se cortó la conexión con el navegador.");
            lock (_pending)
            {
                foreach (var p in _pending.Values) p.TrySetException(new BrowserException("Se cortó la conexión con el navegador."));
                _pending.Clear();
            }
        }
    }

    private static Task SendAsync(ClientWebSocket ws, JsonNode message) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
}
