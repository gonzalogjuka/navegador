using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Navegador.Core.Voz;

namespace Navegador.Meeting;

/// <summary>
/// Traductor incorporado de Astro (fase 8.1): un programa aparte, nativo (voz\astro-voz.exe: Whisper + detector de voz
/// + voz inglesa de Windows, todo local). Astro lo lanza cuando una pestaña entra en modo reunión (empieza a cargar
/// antes de que hables) y lo cierra un rato después de que termina la última: fuera de las reuniones no hay proceso ni
/// memoria. Hablan por un named pipe en binario (<see cref="VozProtocol"/>), sólo para este usuario.
/// Si el traductor falta, falla o se cae, la reunión lleva la voz original; si se cae en medio de una reunión se vuelve a
/// lanzar solo. Traduce sólo con el procesador: nunca usa la placa de video.
/// </summary>
public sealed class VozTranslator(AppServices app) : IMeetingTranslator
{
    private static readonly TimeSpan StopAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private const int MaxRestarts = 3;
    /// <summary>Voz en cola hacia el traductor: ~10 s. Si no la lee, se descarta la más vieja.</summary>
    private const int MaxQueuedFrames = 512;

    private sealed class Session(Process process, NamedPipeServerStream pipe)
    {
        public Process Process { get; } = process;
        public NamedPipeServerStream Pipe { get; } = pipe;
        public CancellationTokenSource Cancel { get; } = new();
        public Channel<byte[]> Outgoing { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(MaxQueuedFrames) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public bool Stopping { get; set; }
        public string? FatalError { get; set; }
    }

    private Session? _session;
    private int _restarts;
    private DispatcherTimer? _stopTimer;
    private HashSet<int> _liveTabs = [];

    public string Name => "Traductor de Astro";
    public bool Ready { get; private set; }
    /// <summary>Lo que está haciendo, para la barra de estado: null si está listo o apagado.</summary>
    public string? Status { get; private set; }
    public event Action? Changed;

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "voz", "astro-voz.exe");
    public static bool Available => File.Exists(ExePath);

    /// <summary>Proceso del traductor mientras corre (para contar su memoria en el tope de RAM).</summary>
    public int? ProcessId { get; private set; }

    /// <summary>Lanza el traductor si no estaba. En el hilo de la interfaz.</summary>
    public void Start()
    {
        CancelStop();
        if (_session is not null) return;
        _restarts = 0;
        Launch();
    }

    private void Launch()
    {
        if (!Available)
        {
            SetStatus("Falta el traductor de Astro (voz\\astro-voz.exe)");
            app.Log.Event("voz_error", ("message", "falta voz/astro-voz.exe"));
            return;
        }
        SetStatus("Preparando la traducción…");
        var pipeName = "astro-voz-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1 << 16, 1 << 16);
        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]
                 {
                     "--pipe", pipeName, "--models", app.Paths.VozModels, "--parent", Environment.ProcessId.ToString(),
                 })
            start.ArgumentList.Add(arg);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new Win32Exception("no arrancó");
        }
        catch (Win32Exception ex)
        {
            pipe.Dispose();
            SetStatus("No se pudo preparar la traducción");
            app.Log.Event("voz_error", ("message", "no arrancó astro-voz.exe: " + ex.Message));
            return;
        }
        var session = new Session(process, pipe);
        _session = session;
        ProcessId = process.Id;
        app.Log.Event("voz_start", ("pid", process.Id), ("restart", _restarts));
        _ = Task.Run(() => PumpAsync(session));
    }

    // ---------- Pipe (fuera del hilo de la interfaz) ----------

    private async Task PumpAsync(Session session)
    {
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(session.Cancel.Token))
            {
                connect.CancelAfter(ConnectTimeout);
                await session.Pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
            }
            var writer = WriteLoopAsync(session);
            while (await VozProtocol.ReadAsync(session.Pipe, session.Cancel.Token).ConfigureAwait(false) is (var type, var payload))
                Handle(session, type, payload);
            session.Cancel.Cancel();
            await writer.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException
                                       or System.Text.Json.JsonException)
        {
            // El traductor cerró, se cayó o mandó algo fuera del protocolo: se trata igual, como que terminó.
        }
        // Acá, fuera del hilo de la interfaz, se espera a que el proceso termine (o se lo cierra).
        int? exitCode = null;
        try
        {
            if (!session.Process.WaitForExit(session.Stopping ? 3000 : 1000)) session.Process.Kill();
            session.Process.WaitForExit(2000);
            exitCode = session.Process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        session.Cancel.Cancel();
        session.Pipe.Dispose();
        session.Process.Dispose();
        app.Log.Event("voz_exit", ("exit_code", exitCode), ("stopping", session.Stopping));
        OnUi(() => OnEnded(session));
    }

    private static async Task WriteLoopAsync(Session session)
    {
        try
        {
            await foreach (var frame in session.Outgoing.Reader.ReadAllAsync(session.Cancel.Token).ConfigureAwait(false))
                await session.Pipe.WriteAsync(frame, session.Cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void Handle(Session session, VozFrame type, byte[] payload)
    {
        switch (type)
        {
            case VozFrame.Play:
                var (tab, rate, pcm) = VozProtocol.ParsePlay(payload);
                app.Meetings.Play(tab, pcm.ToArray(), rate); // directo a la pestaña: no espera a nada más
                break;
            case VozFrame.Status:
                var status = VozProtocol.Parse<VozStatus>(payload);
                OnUi(() =>
                {
                    if (_session != session) return;
                    SetStatus(status.Phase == "downloading"
                        ? $"Preparando la traducción (una sola vez): bajando {status.Detail} {status.Progress * 100:0} %"
                        : $"Preparando la traducción: {status.Detail}…");
                });
                break;
            case VozFrame.Ready:
                var ready = VozProtocol.Parse<VozReady>(payload);
                OnUi(() =>
                {
                    if (_session != session) return;
                    Ready = true;
                    app.Log.Event("voz_ready", ("load_ms", ready.LoadMs), ("asr", ready.Asr),
                        ("voice", ready.Voice), ("threads", ready.Threads), ("pid", session.Process.Id),
                        ("high_priority", ready.HighPriority), ("mmcss", ready.Mmcss));
                    SetStatus(null);
                    app.Meetings.OfferBuiltIn(this);
                });
                break;
            case VozFrame.Phrase:
                var p = VozProtocol.Parse<VozPhrase>(payload);
                app.Log.Event("voz_phrase", ("tab_id", p.Tab), ("spoken", p.Spoken), ("reason", p.Reason), ("audio_ms", p.AudioMs),
                    ("asr_ms", p.AsrMs), ("tts_ms", p.TtsMs), ("total_ms", p.TotalMs), ("merged", p.Merged), ("pending", p.Pending), ("chars", p.Chars));
                break;
            case VozFrame.Stats:
                var stats = VozProtocol.Parse<VozStats>(payload);
                app.Log.Event("voz_stats", ("mic_frames", stats.MicFrames), ("windows", stats.Windows), ("voiced", stats.Voiced),
                    ("phrases", stats.Phrases), ("done", stats.Done));
                break;
            case VozFrame.Error:
                var error = VozProtocol.Parse<VozError>(payload);
                app.Log.Event("voz_error", ("message", error.Message), ("fatal", error.Fatal));
                if (error.Fatal) session.FatalError = error.Message;
                break;
        }
    }

    // ---------- Hilo de la interfaz ----------

    private void OnEnded(Session session)
    {
        if (_session != session) return; // ya lo reemplazó otro, o se pidió apagar
        _session = null;
        ProcessId = null;
        Ready = false;
        app.Meetings.Disconnect(this);

        if (session.FatalError is { } fatal)
        {
            SetStatus(fatal);
        }
        else if (app.Meetings.SessionCount > 0 && _restarts < MaxRestarts)
        {
            // Se cayó en medio de una reunión: vuelve solo.
            _restarts++;
            Launch();
        }
        else
        {
            SetStatus(app.Meetings.SessionCount > 0 ? "No se pudo preparar la traducción" : null);
        }
    }

    /// <summary>Apaga el traductor dentro de un rato (si vuelve una reunión antes, sigue prendido).</summary>
    public void StopSoon()
    {
        if (_session is null) return;
        _stopTimer ??= new DispatcherTimer { Interval = StopAfter };
        _stopTimer.Tick -= OnStopTimer;
        _stopTimer.Tick += OnStopTimer;
        _stopTimer.Start();
    }

    private void OnStopTimer(object? sender, EventArgs e) => Stop();

    private void CancelStop() => _stopTimer?.Stop();

    /// <summary>Lo apaga ya: le avisa (Bye) y, si no termina solo, lo cierra.</summary>
    public void Stop()
    {
        CancelStop();
        if (_session is not { } session)
        {
            SetStatus(null);
            return;
        }
        _session = null;
        ProcessId = null;
        session.Stopping = true;
        session.Outgoing.Writer.TryWrite(VozProtocol.Frame(VozFrame.Bye));
        session.Outgoing.Writer.TryComplete();
        Ready = false;
        _liveTabs = [];
        app.Meetings.Disconnect(this);
        SetStatus(null);
        // El traductor cierra el pipe al recibir Bye y PumpAsync termina la limpieza; si no contesta, se lo cierra.
        _ = Task.Delay(3000).ContinueWith(_ => session.Cancel.Cancel(), TaskScheduler.Default);
    }

    /// <summary>Al salir de Astro: no se espera a nadie.</summary>
    public void Kill()
    {
        CancelStop();
        if (_session is not { } session) return;
        _session = null;
        ProcessId = null;
        session.Stopping = true;
        try { session.Process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    public void OnMicAudio(int tabId, byte[] pcm16k)
    {
        if (Ready && _session is { } session) session.Outgoing.Writer.TryWrite(VozProtocol.Mic(tabId, pcm16k));
    }

    /// <summary>Una pestaña soltó el micrófono o dejó la reunión: lo que tenía de frase a medias sale ya.</summary>
    public void OnMeetingsChanged(IReadOnlyList<MeetingInfo> meetings)
    {
        var live = meetings.Where(m => m.MicLive).Select(m => m.TabId).ToHashSet();
        if (_session is { } session)
            foreach (var tab in _liveTabs.Where(t => !live.Contains(t)))
                session.Outgoing.Writer.TryWrite(VozProtocol.TabClosed(tab));
        _liveTabs = live;
    }

    private void SetStatus(string? status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private static void OnUi(Action action)
    {
        if (Application.Current?.Dispatcher is { HasShutdownStarted: false } dispatcher) dispatcher.BeginInvoke(action);
    }
}
