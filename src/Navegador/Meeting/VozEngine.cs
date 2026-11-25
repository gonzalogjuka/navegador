using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace Navegador.Meeting;

/// <summary>
/// Traductor incorporado de Astro (fase 7.3/7.4): el motor de Voz/engine.js (Whisper + Kokoro, locales) en una
/// página interna oculta. Se prende cuando una pestaña entra en modo reunión (empieza a cargar los modelos antes de que
/// hables) y se apaga un rato después de que termina la última: fuera de las reuniones no hay trabajo ni memoria.
/// Los modelos se bajan de Hugging Face la primera vez y quedan en la caché del perfil.
/// </summary>
public sealed class VozEngine(AppServices app) : IMeetingTranslator
{
    public const string Host = "astro-voz.local";
    private static readonly TimeSpan StopAfter = TimeSpan.FromMinutes(2);

    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private Task? _starting;
    private TaskCompletionSource<JsonElement>? _test;
    private System.Windows.Threading.DispatcherTimer? _stopTimer;
    private HostVoice? _voice;

    public string Name => "Traductor de Astro";
    public bool Ready { get; private set; }
    /// <summary>Lo que está haciendo, para la barra de estado: null si está listo o apagado.</summary>
    public string? Status { get; private set; }
    public string? Backend { get; private set; }
    public event Action? Changed;

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Voz");
    public static bool Available => File.Exists(Path.Combine(Folder, "engine.js"));

    /// <summary>Prende el motor (si no estaba) en el entorno de WebView2 de la ventana.</summary>
    public Task StartAsync(CoreWebView2Environment env, Window owner, string? query = null)
    {
        CancelStop();
        return _starting ??= StartCoreAsync(env, owner, query);
    }

    private async Task StartCoreAsync(CoreWebView2Environment env, Window owner, string? query)
    {
        if (!Available)
        {
            SetStatus("Falta el motor de traducción (Voz\\engine.js)");
            app.Log.Event("voz_error", ("message", "falta Voz/engine.js"));
            return;
        }
        SetStatus("Preparando la traducción…");
        // Voz de Windows si hay una en inglés (instantánea); si no, el motor baja y usa Kokoro.
        _voice ??= await Task.Run(HostVoice.TryCreate);
        var options = (query ?? "").TrimStart('?');
        if (_voice is not null && !options.Contains("tts=")) options = (options.Length > 0 ? options + "&" : "") + "tts=host";
        query = options.Length > 0 ? "?" + options : null;
        var hwnd = new WindowInteropHelper(owner).EnsureHandle();
        _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
        // "Visible" en 1x1 píxel transparente: oculta (IsVisible = false), Windows y Chromium le bajan la prioridad y
        // Whisper tarda el doble (~2,1 s contra ~0,9 s por frase, medido en Iris Xe).
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, 1, 1);
        _controller.IsVisible = true;
        _controller.DefaultBackgroundColor = System.Drawing.Color.Transparent; // ni el píxel se nota
        _core = _controller.CoreWebView2;
        var s = _core.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = true; // página propia: es el único canal con el motor
        _core.SetVirtualHostNameToFolderMapping(Host, Folder, CoreWebView2HostResourceAccessKind.Deny);
        _core.WebMessageReceived += OnMessage;
        _core.ProcessFailed += (_, e) => OnProcessFailed(e.ProcessFailedKind);
        _core.Navigate($"https://{Host}/engine.html{query}");
        app.Log.Event("voz_start", ("query", query));
    }

    /// <summary>
    /// Los procesos auxiliares de WebView2 (audio, red, utilidades) son de todas las pestañas: que se caiga uno (lo hace
    /// Jitsi, por ejemplo) no es problema del motor. Si se cae su página o la GPU, se recarga sola (~3 s con los modelos
    /// en caché); mientras tanto la reunión escucha la voz original.
    /// </summary>
    private void OnProcessFailed(CoreWebView2ProcessFailedKind kind)
    {
        app.Log.Event("voz_process_failed", ("kind", kind.ToString()));
        if (kind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.GpuProcessExited or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)) return;
        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited || _core is null) { Stop(); return; }
        Ready = false;
        app.Meetings.Disconnect(this);
        SetStatus("Preparando la traducción…");
        try { _core.Reload(); } catch (InvalidOperationException) { Stop(); }
    }

    /// <summary>Apaga el motor dentro de un rato (si vuelve una reunión antes, sigue prendido).</summary>
    public void StopSoon()
    {
        if (_controller is null) return;
        _stopTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = StopAfter };
        _stopTimer.Tick -= OnStopTimer;
        _stopTimer.Tick += OnStopTimer;
        _stopTimer.Start();
    }

    private void OnStopTimer(object? sender, EventArgs e) => Stop();

    private void CancelStop() => _stopTimer?.Stop();

    public void Stop()
    {
        CancelStop();
        if (_controller is null) return;
        app.Meetings.Disconnect(this);
        try { _controller.Close(); } catch (InvalidOperationException) { }
        _controller = null;
        _core = null;
        _starting = null;
        Ready = false;
        SetStatus(null);
        app.Log.Event("voz_stop");
    }

    public void OnMicAudio(int tabId, byte[] pcm16k)
    {
        if (!Ready || _core is null) return;
        Post(new { t = "mic", tab = tabId, pcm = Convert.ToBase64String(pcm16k) });
    }

    /// <summary>Prueba (sólo --automation): traduce un archivo de audio entero y devuelve los tiempos por frase.</summary>
    public async Task<JsonElement> TestAsync(byte[] audio, TimeSpan timeout)
    {
        _test = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new { t = "test", audio = Convert.ToBase64String(audio) });
        return await _test.Task.WaitAsync(timeout);
    }

    private void Post(object message)
    {
        try { _core?.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void SetStatus(string? status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone(); }
        catch (JsonException) { return; }
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("t", out var t)) return;
        switch (t.GetString())
        {
            case "hello":
                Post(new { t = "start" }); // empieza a cargar los modelos ya, antes de que hables
                break;
            case "status":
                var detail = m.TryGetProperty("detail", out var d) ? d.GetString() : null;
                if (m.GetProperty("phase").GetString() == "downloading" && m.TryGetProperty("progress", out var p))
                    SetStatus($"Preparando la traducción (una sola vez): bajando {detail} {p.GetDouble() * 100:0} %");
                else
                    SetStatus($"Preparando la traducción: {detail}…");
                break;
            case "ready":
                Ready = true;
                Backend = m.GetProperty("backend").GetString();
                app.Log.Event("voz_ready", ("backend", Backend), ("load_ms", m.GetProperty("loadMs").GetInt32()),
                    ("asr", m.GetProperty("asr").GetString()), ("tts", m.GetProperty("tts").GetString()), ("voice", _voice?.VoiceName));
                SetStatus(null);
                app.Meetings.OfferBuiltIn(this);
                break;
            case "play":
                app.Meetings.Play(m.GetProperty("tab").GetInt32(), Convert.FromBase64String(m.GetProperty("pcm").GetString()!),
                    m.GetProperty("rate").GetInt32());
                break;
            case "speak":
            {
                var tab = m.GetProperty("tab").GetInt32();
                var text = m.GetProperty("en").GetString() ?? "";
                var voice = _voice;
                if (voice is null || text.Length == 0) break;
                _ = Task.Run(() =>
                {
                    var pcm = voice.Speak(text);
                    app.Meetings.Play(tab, pcm, HostVoice.Rate);
                });
                break;
            }
            case "phrase":
                app.Log.Event("voz_phrase", ("tab_id", m.GetProperty("tab").GetInt32()), ("audio_ms", m.GetProperty("audioMs").GetInt32()),
                    ("asr_ms", m.GetProperty("asrMs").GetInt32()), ("tts_ms", m.GetProperty("ttsMs").GetInt32()),
                    ("total_ms", m.GetProperty("totalMs").GetInt32()), ("merged", m.GetProperty("merged").GetInt32()));
                break;
            case "test-result":
                _test?.TrySetResult(m);
                break;
            case "error":
                var message = m.GetProperty("message").GetString();
                app.Log.Event("voz_error", ("message", message));
                if (!Ready) SetStatus("No se pudo preparar la traducción: va tu voz original");
                _test?.TrySetException(new InvalidOperationException(message));
                break;
        }
    }
}
