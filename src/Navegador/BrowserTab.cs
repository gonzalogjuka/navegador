using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Wpf;
using Navegador.Core;

namespace Navegador;

public sealed class BrowserTab : INotifyPropertyChanged
{
    private static int _nextId;

    private string _title = "Nueva pestaña";
    private string? _faviconUri;
    private TabState _state;
    private bool _isActive;
    private bool _isLoading;

    public int Id { get; } = Interlocked.Increment(ref _nextId);
    public string Url { get; set; } = "about:blank";
    /// <summary>Host del documento principal, incluso mientras navega (para no bloquear al propio sitio).</summary>
    public string? TopHost { get; set; }
    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;
    public bool PlayingAudio { get; set; }
    /// <summary>Hasta cuándo no se descarta: tenía un formulario escrito sin enviar la última vez que se intentó.</summary>
    public DateTime KeepAliveUntilUtc { get; set; }
    /// <summary>Si tenía un formulario escrito sin enviar cuando se suspendió (suspendida no contesta scripts).</summary>
    public bool UnsavedInputWhenSuspended { get; set; }
    public int BlockedOnPage { get; set; }
    /// <summary>CoreWebView2 o null si no hay WebView o su proceso murió (el getter de WebView2 tira en ese caso).</summary>
    public Microsoft.Web.WebView2.Core.CoreWebView2? Core
    {
        get
        {
            try { return WebView?.CoreWebView2; }
            catch (InvalidOperationException) { return null; }
        }
    }
    public WebView2? WebView { get; set; }
    /// <summary>Scroll guardado al dejar la pestaña; se reaplica al restaurarla.</summary>
    public string? SavedScroll { get; set; }
    public Core.EventLog.Span? RestoreSpan { get; set; }
    /// <summary>Página propia que muestra (about:blank, about:history) o null si es un sitio.</summary>
    public string? InternalPage { get; set; }
    /// <summary>Host que HTTPS primero acaba de subir a https y URL http para volver si falla.</summary>
    public string? UpgradeHost { get; set; }
    public string? UpgradeFallback { get; set; }
    /// <summary>Navegación http cancelada para subirla a https; su NavigationCompleted (cancelada) se ignora.</summary>
    public ulong? CancelledNavigationId { get; set; }
    /// <summary>Última navegación iniciada; los avisos de navegaciones anteriores (interrumpidas) se ignoran.</summary>
    public ulong? CurrentNavigationId { get; set; }
    /// <summary>Archivo de snapshot (nombre dentro de la carpeta snapshots) o null.</summary>
    public string? SnapshotFile { get; set; }
    /// <summary>Despertando con snapshot: el WebView queda oculto hasta que la página real cargó y recuperó el scroll.</summary>
    public bool RevealPending { get; set; }
    /// <summary>Último scroll conocido de la página actual (se guarda en la sesión). SavedScroll es el que falta reaplicar.</summary>
    public string? LastScroll { get; set; }

    public string Title { get => _title; set => Set(ref _title, string.IsNullOrWhiteSpace(value) ? Url : value); }
    public string? FaviconUri { get => _faviconUri; set => Set(ref _faviconUri, string.IsNullOrEmpty(value) ? null : value); }
    public TabState State { get => _state; set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(IsSleeping)); OnPropertyChanged(nameof(CanSleep)); } } }
    public bool IsActive { get => _isActive; set { if (Set(ref _isActive, value)) OnPropertyChanged(nameof(CanSleep)); } }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public bool IsSleeping => _state != TabState.Live;
    /// <summary>"Dormir ahora" del menú de la pestaña: no aplica a la activa ni a una ya descartada.</summary>
    public bool CanSleep => !_isActive && _state != TabState.Discarded;

    /// <summary>Modo reunión: micrófono virtual inyectado y canal de audio con el navegador (ver MainWindow.Meeting.cs).</summary>
    public bool InMeeting { get => _inMeeting; set => Set(ref _inMeeting, value); }
    private bool _inMeeting;
    public MeetingVoice MeetingVoice { get; set; } = MeetingVoice.Auto;
    public string? MeetingScriptId { get; set; }
    public EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs>? MeetingHandler { get; set; }
    /// <summary>La página tomó el micrófono (virtual) y todavía no lo soltó.</summary>
    public bool MeetingMicLive { get; set; }
    public long MeetingMicChunks { get; set; }
    public long MeetingPlayChunks { get; set; }

    public TabSnapshot Snapshot() => new(Id, IsActive, State, LastActiveUtc, PlayingAudio, KeepAliveUntilUtc > DateTime.UtcNow, InMeeting);

    public static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? u.Host.ToLowerInvariant() : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
