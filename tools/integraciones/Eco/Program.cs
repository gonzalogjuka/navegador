// Cliente de ejemplo de la API local del navegador (docs/API.md): hace de "traductor".
//   dotnet run --project tools/integraciones/Eco -- [--tone | --echo] [--data-dir <carpeta>] [--token-file <archivo>] [--seconds N]
// --tone  (por defecto) contesta cada pedazo de tu voz con la misma duración de un tono de 440 Hz.
// --echo  devuelve tu propia voz: en la reunión te escuchan igual, pero pasando por la app (sirve para medir la demora).
// La primera vez el navegador muestra "«Eco» quiere conectarse…": al aprobar, el token queda en --token-file.
// Tu traductor real hace lo mismo, pero en vez del tono manda la voz traducida (PCM 16 bits mono, a la frecuencia
// que quiera: lo dice en la cabecera de cada paquete).
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var mode = args.Contains("--echo") ? "echo" : "tone";
string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var dataDir = Arg("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Navegador");
var tokenFile = Arg("--token-file")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NavegadorEco", "token.txt");
var seconds = int.TryParse(Arg("--seconds"), out var s) ? s : 0;

// El navegador deja su dirección en api.json mientras está abierto.
var apiInfo = Path.Combine(dataDir, "api.json");
for (var i = 0; i < 100 && !File.Exists(apiInfo); i++) await Task.Delay(200);
if (!File.Exists(apiInfo)) { Console.Error.WriteLine($"No encuentro {apiInfo}: ¿está abierto el navegador?"); return 2; }
var url = JsonDocument.Parse(File.ReadAllText(apiInfo)).RootElement.GetProperty("url").GetString()!;

using var ws = new ClientWebSocket();
using var stop = new CancellationTokenSource();
if (seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
await ws.ConnectAsync(new Uri(url), stop.Token);
var token = File.Exists(tokenFile) ? File.ReadAllText(tokenFile).Trim() : null;
await SendJson(new { type = "hello", app = "Eco", token, wants = new[] { "meeting.audio" } });
Console.WriteLine($"conectado a {url}; esperando permiso…");

var phase = 0.0;
long received = 0;
var buffer = new byte[256 * 1024];
try
{
    while (!stop.IsCancellationRequested)
    {
        var (type, data) = await Receive();
        if (type == WebSocketMessageType.Close) break;
        if (type == WebSocketMessageType.Text)
        {
            var m = JsonDocument.Parse(data).RootElement;
            var kind = m.GetProperty("type").GetString();
            Console.WriteLine($"<- {Encoding.UTF8.GetString(data)}");
            if (kind == "welcome" && m.TryGetProperty("token", out var t))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
                File.WriteAllText(tokenFile, t.GetString());
            }
            if (kind == "denied") return 3;
            continue;
        }
        // Paquete de audio: [tipo][pestaña int32][frecuencia int32][PCM 16 bits mono little-endian]
        if (data.Length < 9 || data[0] != 1) continue;
        received++;
        var tab = BitConverter.ToInt32(data, 1);
        var reply = new byte[data.Length];
        reply[0] = 2; // voz para la reunión
        BitConverter.TryWriteBytes(reply.AsSpan(1), tab);
        BitConverter.TryWriteBytes(reply.AsSpan(5), 16000);
        if (mode == "echo") Array.Copy(data, 9, reply, 9, data.Length - 9);
        else
            for (var i = 9; i + 1 < reply.Length; i += 2)
            {
                BitConverter.TryWriteBytes(reply.AsSpan(i), (short)(Math.Sin(phase) * 0.5 * short.MaxValue));
                phase = (phase + 2 * Math.PI * 440 / 16000) % (2 * Math.PI);
            }
        await ws.SendAsync(reply, WebSocketMessageType.Binary, true, stop.Token);
    }
}
catch (OperationCanceledException) { }
catch (WebSocketException ex) { Console.WriteLine($"conexión cerrada: {ex.Message}"); }
Console.WriteLine($"fin: {received} pedazos de voz recibidos y contestados");
if (ws.State == WebSocketState.Open)
    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
return 0;

async Task SendJson(object message) =>
    await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, stop.Token);

async Task<(WebSocketMessageType, byte[])> Receive()
{
    using var ms = new MemoryStream();
    while (true)
    {
        var r = await ws.ReceiveAsync(buffer, stop.Token);
        if (r.MessageType == WebSocketMessageType.Close) return (r.MessageType, []);
        ms.Write(buffer, 0, r.Count);
        if (r.EndOfMessage) return (r.MessageType, ms.ToArray());
    }
}
