using System.Windows;

namespace Navegador.Meeting;

/// <summary>
/// Destino del audio de las reuniones: la app de traducción conectada (por la API local, fase 6.2) o, en las pruebas,
/// el tono de prueba. Recibe la voz original de cada pestaña en reunión y devuelve la traducida con
/// <see cref="MeetingHub.Play"/>, desde cualquier hilo.
/// </summary>
public interface IMeetingTranslator
{
    string Name { get; }
    /// <summary>Voz original: PCM 16 kHz mono, 16 bits little-endian. Se llama en el hilo de la interfaz: no bloquear.</summary>
    void OnMicAudio(int tabId, byte[] pcm16k);
    /// <summary>Cambió la lista de pestañas en reunión (o su estado). En el hilo de la interfaz.</summary>
    void OnMeetingsChanged(IReadOnlyList<MeetingInfo> meetings) { }
}

/// <summary>Una pestaña en modo reunión, como la ve el traductor.</summary>
public sealed record MeetingInfo(int TabId, string Title, string? Host, bool MicLive, string Voice);

/// <summary>
/// Une las pestañas en modo reunión (de todas las ventanas) con el traductor conectado. Uno a la vez: conectar otro
/// reemplaza al anterior.
/// </summary>
public sealed class MeetingHub
{
    private readonly Dictionary<int, (MainWindow Window, BrowserTab Tab)> _sessions = [];

    public IMeetingTranslator? Translator { get; private set; }
    public bool TranslatorConnected => Translator is not null;
    public int SessionCount => _sessions.Count;

    /// <summary>Cambió el traductor o una pestaña: las ventanas refrescan la mezcla y la interfaz.</summary>
    public event Action? Changed;

    public void Connect(IMeetingTranslator translator)
    {
        Translator = translator;
        Notify();
    }

    public void Disconnect(IMeetingTranslator translator)
    {
        if (Translator != translator) return;
        // Se fue una app externa: vuelve el traductor incorporado si está listo.
        Translator = BuiltIn is { Ready: true } b && b != translator ? b : null;
        Notify();
    }

    /// <summary>Traductor incorporado de Astro. Una app externa conectada por la API tiene prioridad.</summary>
    public VozTranslator? BuiltIn { get; set; }

    /// <summary>El incorporado quedó listo: se conecta si no hay una app externa.</summary>
    public void OfferBuiltIn(VozTranslator engine)
    {
        BuiltIn = engine;
        if (Translator is null) Connect(engine);
    }

    internal void Join(MainWindow window, BrowserTab tab)
    {
        _sessions[tab.Id] = (window, tab);
        Notify();
    }

    internal void Leave(BrowserTab tab)
    {
        if (_sessions.Remove(tab.Id)) Notify();
    }

    /// <summary>La pestaña en reunión está en una ventana privada.</summary>
    internal bool IsPrivate(int tabId) => _sessions.TryGetValue(tabId, out var s) && s.Window.IsPrivate;

    /// <summary>Algo cambió (traductor, pestañas, micrófono): refrescan las ventanas y se le avisa al traductor.</summary>
    internal void Notify()
    {
        Changed?.Invoke();
        Translator?.OnMeetingsChanged(_sessions.Values
            .Select(s => new MeetingInfo(s.Tab.Id, s.Tab.Title, BrowserTab.HostOf(s.Tab.Url), s.Tab.MeetingMicLive, s.Tab.MeetingVoice.ToString()))
            .ToList());
    }

    internal void MicAudio(BrowserTab tab, byte[] pcm16k)
    {
        tab.MeetingMicChunks++;
        Translator?.OnMicAudio(tab.Id, pcm16k);
    }

    /// <summary>Lo que suena en la reunión: sólo lo recibe el traductor incorporado (es el contexto de su intérprete).</summary>
    internal void RemoteAudio(BrowserTab tab, byte[] pcm16k) => (Translator as VozTranslator)?.OnRemoteAudio(tab.Id, pcm16k);

    /// <summary>El traductor conectado es el incorporado (el único que escucha a los demás).</summary>
    internal bool BuiltInConnected => Translator is VozTranslator;

    /// <summary>Voz traducida para la pestaña: PCM mono, 16 bits little-endian, a <paramref name="sampleRate"/> Hz.</summary>
    public void Play(int tabId, byte[] pcm, int sampleRate)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_sessions.TryGetValue(tabId, out var s)) s.Window.PlayTranslated(s.Tab, pcm, sampleRate);
        });
    }
}

/// <summary>
/// Traductor de prueba: por cada pedazo de voz que llega devuelve la misma duración de un tono de 440 Hz. Si el tono
/// llega a la reunión, funcionan los dos sentidos (micrófono → navegador → traductor → micrófono virtual).
/// </summary>
public sealed class TestToneTranslator(MeetingHub hub) : IMeetingTranslator
{
    public const double Frequency = 440;
    private const int Rate = 16000;
    private double _phase;

    public string Name => "Tono de prueba";

    public void OnMicAudio(int tabId, byte[] pcm16k)
    {
        var samples = pcm16k.Length / 2;
        var output = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (short)(Math.Sin(_phase) * 0.5 * short.MaxValue);
            _phase += 2 * Math.PI * Frequency / Rate;
            output[2 * i] = (byte)v;
            output[2 * i + 1] = (byte)(v >> 8);
        }
        _phase %= 2 * Math.PI;
        hub.Play(tabId, output, Rate);
    }
}
