using Navegador.Core;

namespace Navegador.Tests;

public class IntegrationStoreTests
{
    [Fact]
    public void Granted_token_is_found_and_only_a_hash_is_stored()
    {
        var store = new IntegrationStore();
        var token = store.Grant("Traductor", ["meeting.audio"], DateTime.UtcNow);
        var grant = store.Find(token);
        Assert.NotNull(grant);
        Assert.Equal("Traductor", grant!.App);
        Assert.Contains("meeting.audio", grant.Scopes);
        Assert.DoesNotContain(store.Grants, g => g.TokenSha256.Contains(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-existe")]
    public void Unknown_token_is_rejected(string token)
    {
        var store = new IntegrationStore();
        store.Grant("Traductor", ["meeting.audio"], DateTime.UtcNow);
        Assert.Null(store.Find(token));
    }

    [Fact]
    public void Survives_save_and_load_and_revoke_all_forgets_everything()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nav-int-{Guid.NewGuid():N}.json");
        try
        {
            var store = new IntegrationStore();
            var token = store.Grant("Traductor", ["meeting.audio"], DateTime.UtcNow);
            store.Save(path);
            var loaded = IntegrationStore.Load(path);
            Assert.NotNull(loaded.Find(token));
            loaded.RevokeAll();
            Assert.Null(loaded.Find(token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Corrupt_file_loads_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nav-int-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{no es json");
        try { Assert.Empty(IntegrationStore.Load(path).Grants); }
        finally { File.Delete(path); }
    }
}

public class AudioFrameTests
{
    [Fact]
    public void Round_trips_kind_tab_rate_and_pcm()
    {
        byte[] pcm = [1, 2, 3, 4];
        var frame = AudioFrame.Encode(AudioFrame.Play, 42, 24000, pcm);
        Assert.True(AudioFrame.TryDecode(frame, out var kind, out var tab, out var rate, out var data));
        Assert.Equal(AudioFrame.Play, kind);
        Assert.Equal(42, tab);
        Assert.Equal(24000, rate);
        Assert.Equal(pcm, data);
    }

    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0 })]                          // corto
    [InlineData(new byte[] { 9, 1, 0, 0, 0, 0x80, 0x3E, 0, 0, 0, 0 })] // tipo desconocido
    [InlineData(new byte[] { 2, 1, 0, 0, 0, 0x10, 0, 0, 0, 0, 0 })]    // 16 Hz: fuera de rango
    [InlineData(new byte[] { 2, 1, 0, 0, 0, 0x80, 0x3E, 0, 0, 0 })]    // 16 kHz pero cantidad impar de bytes
    public void Rejects_malformed_frames(byte[] frame) =>
        Assert.False(AudioFrame.TryDecode(frame, out _, out _, out _, out _));
}

public class WebSocketHandshakeTests
{
    private const string Key = "dGhlIHNhbXBsZSBub25jZQ==";

    private static string Request(string host = "localhost:47800", string? origin = null, string path = "/v1") =>
        $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
        $"Sec-WebSocket-Key: {Key}\r\nSec-WebSocket-Version: 13\r\n" + (origin is null ? "" : $"Origin: {origin}\r\n") + "\r\n";

    [Theory]
    [InlineData("localhost:47800")]
    [InlineData("127.0.0.1:47800")]
    [InlineData("[::1]:47800")]
    public void Accepts_local_apps(string host)
    {
        var r = WebSocketHandshake.Check(Request(host));
        Assert.True(r.Ok, r.Reason);
        Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", r.Response); // ejemplo del RFC 6455
    }

    [Fact]
    public void Rejects_web_pages_which_always_send_origin() =>
        Assert.False(WebSocketHandshake.Check(Request(origin: "https://evil.example")).Ok);

    [Fact]
    public void Rejects_other_hosts_against_dns_rebinding() =>
        Assert.False(WebSocketHandshake.Check(Request(host: "evil.example:47800")).Ok);

    [Fact]
    public void Rejects_unknown_paths_and_plain_http()
    {
        Assert.False(WebSocketHandshake.Check(Request(path: "/otra")).Ok);
        Assert.False(WebSocketHandshake.Check("GET /v1 HTTP/1.1\r\nHost: localhost\r\n\r\n").Ok);
    }
}
