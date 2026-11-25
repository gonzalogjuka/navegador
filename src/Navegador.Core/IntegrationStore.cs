using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Navegador.Core;

/// <summary>Permiso que la persona le dio a una app local. Del token sólo se guarda su SHA-256.</summary>
public sealed record IntegrationGrant(string App, string TokenSha256, List<string> Scopes, DateTime CreatedUtc);

/// <summary>
/// Apps locales autorizadas a usar la API (integrations.json). Una app se conecta la primera vez sin token, la persona
/// la aprueba en el navegador y recibe un token que guarda para las próximas veces.
/// </summary>
public sealed class IntegrationStore
{
    /// <summary>Recibir la voz de las pestañas en reunión y mandar la voz traducida.</summary>
    public const string MeetingAudio = "meeting.audio";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<IntegrationGrant> Grants { get; set; } = [];

    /// <summary>Crea un token nuevo para la app y lo devuelve (es la única vez que se ve en claro).</summary>
    public string Grant(string app, IEnumerable<string> scopes, DateTime nowUtc)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Grants.Add(new IntegrationGrant(app, Hash(token), scopes.Distinct().ToList(), nowUtc));
        return token;
    }

    public IntegrationGrant? Find(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var h = Encoding.ASCII.GetBytes(Hash(token));
        return Grants.FirstOrDefault(g => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(g.TokenSha256), h));
    }

    public void RevokeAll() => Grants.Clear();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static IntegrationStore Load(string path)
    {
        try
        {
            var s = File.Exists(path) ? JsonSerializer.Deserialize<IntegrationStore>(File.ReadAllText(path)) : null;
            if (s is not null) s.Grants ??= [];
            return s ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// Paquete binario de audio de la API: [tipo 1 byte][pestaña int32][frecuencia int32][PCM mono 16 bits], todo
/// little-endian. Mic = voz original (navegador → app, siempre 16 kHz). Play = voz traducida (app → navegador).
/// </summary>
public static class AudioFrame
{
    public const byte Mic = 1;
    public const byte Play = 2;
    public const int HeaderSize = 9;

    public static byte[] Encode(byte kind, int tabId, int sampleRate, ReadOnlySpan<byte> pcm)
    {
        var frame = new byte[HeaderSize + pcm.Length];
        frame[0] = kind;
        BitConverter.TryWriteBytes(frame.AsSpan(1), tabId);
        BitConverter.TryWriteBytes(frame.AsSpan(5), sampleRate);
        pcm.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, out byte kind, out int tabId, out int sampleRate, out byte[] pcm)
    {
        kind = 0; tabId = 0; sampleRate = 0; pcm = [];
        if (frame.Length < HeaderSize || frame[0] is not (Mic or Play)) return false;
        sampleRate = BitConverter.ToInt32(frame[5..]);
        if (sampleRate is < 8000 or > 48000 || (frame.Length - HeaderSize) % 2 != 0) return false;
        kind = frame[0];
        tabId = BitConverter.ToInt32(frame[1..]);
        pcm = frame[HeaderSize..].ToArray();
        return true;
    }
}

/// <summary>
/// Apertura de WebSocket de la API local (RFC 6455), sin servidor HTTP: sólo GET /v1 con Upgrade, desde la propia PC.
/// Se rechaza cualquier pedido con Origin (lo mandan siempre los navegadores: una página web nunca puede usar la API)
/// y cualquier Host que no sea local (contra DNS rebinding).
/// </summary>
public static class WebSocketHandshake
{
    public readonly record struct Result(bool Ok, string Response, string? Reason);

    public const string Path = "/v1";
    private const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static Result Check(string requestHead)
    {
        var lines = requestHead.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[0] != "GET") return Fail("not_get");
        if (first[1] != Path) return Fail("path");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (headers.ContainsKey("Origin")) return Fail("origin");
        if (!headers.TryGetValue("Host", out var host) || !IsLocalHost(host)) return Fail("host");
        if (!headers.TryGetValue("Upgrade", out var up) || !up.Equals("websocket", StringComparison.OrdinalIgnoreCase)) return Fail("upgrade");
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || key.Length == 0) return Fail("key");
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Magic)));
        return new(true, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                         $"Sec-WebSocket-Accept: {accept}\r\n\r\n", null);

        static Result Fail(string why) => new(false, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", why);
    }

    private static bool IsLocalHost(string host)
    {
        var name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        return name is "localhost" or "127.0.0.1" or "[::1]";
    }
}
