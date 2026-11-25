using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using Navegador.Core;
using Navegador.Meeting;

namespace Navegador.Api;

/// <summary>
/// API local (fase 6.2): WebSocket en ws://localhost:&lt;ApiPort&gt;/v1, sólo en la interfaz de loopback. Protocolo en
/// docs/API.md. Cada app manda "hello"; si no trae un token válido, la persona la aprueba o rechaza en el navegador y
/// recibe un token para las próximas veces (integrations.json guarda sólo su hash). Una página web no puede conectarse
/// (se rechaza todo pedido con Origin), y una app sin permiso no recibe nada.
/// </summary>
public sealed class ApiServer(AppServices app) : IDisposable
{
    private const int MaxMessageBytes = 1 << 20;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly List<ApiSession> _sessions = [];
    private IntegrationStore _store = new();

    public int Port { get; private set; }

    /// <summary>La interfaz muestra el pedido de permiso y devuelve si la persona lo aprobó.</summary>
    public Func<string, IReadOnlyList<string>, Task<bool>>? Approve { get; set; }

    public static readonly IReadOnlyDictionary<string, string> ScopeDescriptions = new Dictionary<string, string>
    {
        [IntegrationStore.MeetingAudio] = "recibir tu voz en las pestañas con modo reunión y mandar la voz traducida",
    };

    public void Start()
    {
        _store = IntegrationStore.Load(app.Paths.Integrations);
        Port = app.Settings.ApiPort;
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                var l = new TcpListener(address, Port);
                l.Start();
                _listeners.Add(l);
                _ = AcceptLoopAsync(l);
            }
            catch (SocketException ex)
            {
                app.Log.Event("api_error", ("where", "listen"), ("address", address.ToString()), ("port", Port), ("message", ex.Message));
            }
        }
        if (_listeners.Count == 0) return;
        try
        {
            File.WriteAllText(app.Paths.ApiInfo, JsonSerializer.Serialize(new
            {
                url = $"ws://localhost:{Port}{WebSocketHandshake.Path}", port = Port, pid = Environment.ProcessId,
            }));
        }
        catch (IOException) { }
        app.Log.Event("api_listen", ("port", Port), ("listeners", _listeners.Count), ("apps", _store.Grants.Count));
    }

    /// <summary>Olvida todos los permisos y corta las apps conectadas.</summary>
    public void RevokeAll()
    {
        _store.RevokeAll();
        SaveStore();
        lock (_sessions) foreach (var s in _sessions) s.Abort();
        app.Log.Event("api_revoke_all");
    }

    public int GrantCount => _store.Grants.Count;

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true; // audio en tiempo real: sin esperar a juntar paquetes
        var stream = client.GetStream();
        string head;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            head = await ReadHeadAsync(stream, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { return; }

        var check = WebSocketHandshake.Check(head);
        var response = Encoding.ASCII.GetBytes(check.Response);
        try { await stream.WriteAsync(response, _cts.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return; }
        if (!check.Ok)
        {
            app.Log.Event("api_rejected", ("reason", check.Reason));
            return;
        }

        using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20),
        });
        var session = new ApiSession(this, app, ws);
        lock (_sessions) _sessions.Add(session);
        try { await session.RunAsync(_cts.Token); }
        finally { lock (_sessions) _sessions.Remove(session); }
    }

    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var n = 0;
        while (n < buf.Length)
        {
            var read = await stream.ReadAsync(buf.AsMemory(n, 1), ct); // de a un byte: no consumir datos del WebSocket
            if (read == 0) throw new IOException("cerrado");
            n++;
            if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n')
                return Encoding.ASCII.GetString(buf, 0, n - 4);
        }
        throw new InvalidDataException("cabecera demasiado larga");
    }

    internal IntegrationGrant? FindGrant(string? token) => _store.Find(token);

    internal string Grant(string appName, IReadOnlyList<string> scopes)
    {
        var token = _store.Grant(appName, scopes, DateTime.UtcNow);
        SaveStore();
        return token;
    }

    internal async Task<bool> RequestApprovalAsync(string appName, IReadOnlyList<string> scopes)
    {
        if (Approve is not { } approve) return false;
        var asked = await Application.Current.Dispatcher.InvokeAsync(() => approve(appName, scopes));
        var answer = await Task.WhenAny(asked, Task.Delay(TimeSpan.FromMinutes(2), _cts.Token).ContinueWith(_ => false));
        return await answer;
    }

    private void SaveStore()
    {
        try { _store.Save(app.Paths.Integrations); }
        catch (IOException ex) { app.Log.Event("api_error", ("where", "save_integrations"), ("message", ex.Message)); }
    }

    internal static int MaxMessage => MaxMessageBytes;

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var l in _listeners) l.Stop();
        lock (_sessions) foreach (var s in _sessions) s.Abort();
        try { File.Delete(app.Paths.ApiInfo); } catch (IOException) { }
    }
}

/// <summary>Una app conectada. Si tiene el permiso meeting.audio, es el traductor de las reuniones.</summary>
internal sealed class ApiSession(ApiServer server, AppServices app, WebSocket ws) : IMeetingTranslator
{
    // Audio en tiempo real: si la app no da abasto, se tira lo más viejo antes que acumular retraso.
    private readonly Channel<(bool Text, byte[] Data)> _out = Channel.CreateBounded<(bool, byte[])>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private string _name = "?";
    private long _micSent, _playReceived;

    public string Name => _name;

    public void Abort() => ws.Abort();

    public async Task RunAsync(CancellationToken ct)
    {
        var connected = false;
        var send = SendLoopAsync(ct);
        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var (isText, hello) = await ReceiveAsync(helloTimeout.Token);
            if (!isText || !TryParseHello(hello, out var name, out var token, out var wants)) { Send(new { type = "error", error = "se esperaba hello" }); return; }
            _name = name;

            string[] scopes;
            if (server.FindGrant(token) is { } grant)
            {
                scopes = grant.Scopes.ToArray();
                Send(new { type = "welcome", app = _name, scopes });
            }
            else
            {
                var asked = wants.Where(ApiServer.ScopeDescriptions.ContainsKey).Distinct().ToArray();
                app.Log.Event("api_approval_asked", ("app", _name), ("scopes", string.Join(",", asked)));
                if (asked.Length == 0 || !await server.RequestApprovalAsync(_name, asked))
                {
                    app.Log.Event("api_denied", ("app", _name));
                    Send(new { type = "denied" });
                    await Task.Delay(200, ct);
                    return;
                }
                scopes = asked;
                Send(new { type = "welcome", app = _name, scopes, token = server.Grant(_name, asked) });
            }
            app.Log.Event("api_connected", ("app", _name), ("scopes", string.Join(",", scopes)));

            if (scopes.Contains(IntegrationStore.MeetingAudio))
            {
                await Application.Current.Dispatcher.InvokeAsync(() => app.Meetings.Connect(this));
                connected = true;
            }

            while (!ct.IsCancellationRequested)
            {
                var (text, data) = await ReceiveAsync(ct);
                if (!text)
                {
                    if (connected && AudioFrame.TryDecode(data, out var kind, out var tab, out var rate, out var pcm) && kind == AudioFrame.Play)
                    {
                        _playReceived++;
                        app.Meetings.Play(tab, pcm, rate);
                    }
                }
                else if (IsPing(data))
                {
                    Send(new { type = "pong" });
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or JsonException) { }
        finally
        {
            if (connected) await Application.Current.Dispatcher.InvokeAsync(() => app.Meetings.Disconnect(this));
            _out.Writer.TryComplete();
            try { await send; } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
            app.Log.Event("api_disconnected", ("app", _name), ("mic_sent", _micSent), ("play_received", _playReceived));
        }
    }

    public void OnMicAudio(int tabId, byte[] pcm16k)
    {
        _micSent++;
        _out.Writer.TryWrite((false, AudioFrame.Encode(AudioFrame.Mic, tabId, 16000, pcm16k)));
    }

    public void OnMeetingsChanged(IReadOnlyList<MeetingInfo> meetings) =>
        Send(new
        {
            type = "meetings",
            tabs = meetings.Select(m => new { id = m.TabId, title = m.Title, host = m.Host, mic = m.MicLive, voice = m.Voice }),
        });

    private void Send(object message) => _out.Writer.TryWrite((true, JsonSerializer.SerializeToUtf8Bytes(message)));

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var (text, data) in _out.Reader.ReadAllAsync(ct))
        {
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(data, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ct);
        }
        if (ws.State == WebSocketState.Open)
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
    }

    private async Task<(bool Text, byte[] Data)> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) throw new OperationCanceledException("cerrado por la app");
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > ApiServer.MaxMessage) throw new IOException("mensaje demasiado grande");
            if (r.EndOfMessage) return (r.MessageType == WebSocketMessageType.Text, ms.ToArray());
        }
    }

    private static bool TryParseHello(byte[] data, out string name, out string? token, out string[] wants)
    {
        name = ""; token = null; wants = [];
        using var doc = JsonDocument.Parse(data);
        var m = doc.RootElement;
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("type", out var t) || t.GetString() != "hello") return false;
        if (!m.TryGetProperty("app", out var a) || a.GetString() is not { Length: > 0 and <= 80 } appName) return false;
        name = appName;
        if (m.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.String) token = tok.GetString();
        if (m.TryGetProperty("wants", out var w) && w.ValueKind == JsonValueKind.Array)
            wants = w.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
        return true;
    }

    private static bool IsPing(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ping";
        }
        catch (JsonException) { return false; }
    }
}
