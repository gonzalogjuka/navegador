using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Navegador.Core;
using EventLog = Navegador.Core.EventLog;

namespace Navegador;

public partial class MainWindow : Window
{
    private const int SampleEveryTicks = 5;   // tick = 1 s
    private const int SaveEveryTicks = 30;
    private static readonly double[] ZoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    private readonly AppServices _app;
    private readonly Settings _settings;
    private readonly BlockList _blockList;
    private readonly EventLog _log;
    private readonly string? _startUrl;
    private readonly bool _isPrivate;
    private readonly ObservableCollection<BrowserTab> _tabs = [];
    private readonly Stack<string> _closed = new();
    /// <summary>Hosts sin HTTPS: compartidos por las ventanas normales; cada ventana privada tiene los suyos, en memoria.</summary>
    private readonly HashSet<string> _httpExempt;
    /// <summary>Sitios con el bloqueo apagado en esta ventana privada (en memoria; la lista de los ajustes no se lee ni se escribe).</summary>
    private readonly HashSet<string> _privateAllowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private CoreWebView2Environment? _env;
    private BrowserTab? _active;
    private long _ticks;
    private bool _applying;
    private long _blockedTotal;
    private double? _lastPrivateMb;
    private MemoryReading? _lastMemory;
    private BudgetDiscard? _lastBudgetDiscard;
    private Dictionary<int, TimeSpan> _lastCpu = [];
    private DateTime? _lastCpuAtUtc;
    private bool _closing;

    private DateTime? _focusLostSinceUtc;
    private bool _activeLowered;
    private bool _hibernateDonePending;

    private int _recoveries;
    private readonly Queue<DateTime> _recoveryTimes = new();
    private bool _recovering;

    private (WindowState State, WindowStyle Style)? _beforeFullscreen;
    private CoreWebView2? _findCore;

    public MainWindow(AppServices app, string? startUrl, bool isPrivate)
    {
        _app = app;
        _settings = app.Settings;
        _blockList = app.BlockList;
        _log = isPrivate ? EventLog.Off : app.Log; // la ventana privada no deja actividad en el log
        _startUrl = startUrl;
        _isPrivate = isPrivate;
        _httpExempt = isPrivate ? new(StringComparer.OrdinalIgnoreCase) : app.HttpExempt;
        InitializeComponent();
        TabList.ItemsSource = _tabs;
        if (isPrivate)
        {
            Resources["ToolbarBrush"] = new SolidColorBrush(Color.FromRgb(0x3B, 0x2E, 0x58));
            PrivateBadge.Visibility = Visibility.Visible;
        }
        Loaded += OnLoaded;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        _tick.Tick += OnTick;
    }

    private string WindowSuffix => _isPrivate ? "Navegador (privada)" : "Navegador";

    public bool IsPrivate => _isPrivate;

    /// <summary>Pedido de otro arranque (acceso directo, link desde otra app): pestaña nueva y ventana al frente.</summary>
    public async void OpenFromOutside(string? url, bool bringToFront)
    {
        if (bringToFront)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        if (url is not null && _env is not null)
            await ActivateAsync(await NewTabAsync(UrlInput.Resolve(url, _settings.SearchUrl)));
    }

    // ---------- Arranque y cierre ----------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var span = _log.StartSpan("startup", ("private", _isPrivate));
        try
        {
            _env = await CreateEnvironmentAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Falta WebView2 Runtime. Instalalo desde https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "Navegador", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var session = _isPrivate ? null : SessionStore.Load(_app.Paths.Session);
        if (session is not null)
        {
            // Todas descartadas: arrancar no carga páginas que no vas a mirar.
            foreach (var t in session.Tabs)
                AddTab(new BrowserTab
                {
                    Url = t.Url, Title = t.Title, State = TabState.Discarded, LastScroll = t.Scroll,
                    SnapshotFile = SnapshotFiles.IsValidName(t.Snapshot) && File.Exists(Path.Combine(_app.Paths.Snapshots, t.Snapshot!)) ? t.Snapshot : null,
                });
        }
        if (!_isPrivate) DeleteOrphanSnapshots();

        if (_startUrl is not null)
            await ActivateAsync(await NewTabAsync(UrlInput.Resolve(_startUrl, _settings.SearchUrl)));
        else if (session is not null)
            await ActivateAsync(_tabs[session.ActiveIndex]);
        else
            await ActivateAsync(await NewTabAsync(InternalPages.NewTab));

        span.End();
        _log.Event("startup_done", ("tabs", _tabs.Count), ("blocklist_domains", _blockList.Domains.Count), ("private", _isPrivate));
        // Abierta sin foco (detrás de otra app, o en las pruebas ocultas): nunca llega Deactivated, así que la espera
        // de "foco perdido" arranca acá.
        if (!IsActive) OnDeactivated(this, EventArgs.Empty);
        _tick.Start();
        Sample();
        if (_app.TakeAutomationScript() is { } script) await RunAutomationAsync(script);
    }

    private Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        CoreWebView2Environment.CreateAsync(null, _app.Paths.WebViewData,
            new CoreWebView2EnvironmentOptions(_settings.BrowserArguments));

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _tick.Stop();
        SaveSession();
        _log.Event("shutdown", ("tabs", _tabs.Count), ("blocked_total", _blockedTotal), ("private", _isPrivate));
        if (_isPrivate && !Application.Current.Windows.OfType<MainWindow>().Any(w => w != this && w._isPrivate))
        {
            // Última ventana privada: WebView2 borra la carpeta del perfil cuando termina su proceso; la próxima ventana
            // privada usa un perfil nuevo. Si el borrado no llega (cierre forzado), lo hace el próximo arranque.
            try { _tabs.Select(t => t.Core).FirstOrDefault(c => c is not null)?.Profile.Delete(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            _app.RetirePrivateProfile();
        }
        foreach (var t in _tabs) t.WebView?.Dispose();
    }

    private void SaveSession()
    {
        if (_isPrivate || _tabs.Count == 0) return;
        var tabs = _tabs.Select(t => new SavedTab(t.Url, t.Title, t.LastScroll, t.SnapshotFile)).ToList();
        var active = _active is null ? 0 : Math.Max(0, _tabs.IndexOf(_active));
        try { SessionStore.Save(_app.Paths.Session, new SavedSession(tabs, active)); }
        catch (IOException ex) { _log.Event("error", ("where", "save_session"), ("message", ex.Message)); }
        _app.SaveData();
    }

    // ---------- Caídas del motor ----------

    private async void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.Event("process_failed", ("tab_id", tab.Id), ("kind", e.ProcessFailedKind.ToString()),
            ("reason", e.Reason.ToString()), ("exit_code", e.ExitCode), ("process", e.ProcessDescription));

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            await RecoverBrowserAsync();
        }
        else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                 or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            // Murió el renderer de esta pestaña: la activa se recarga, las de fondo quedan descartadas.
            if (tab == _active) tab.Core?.Reload();
            else { DisposeWebView(tab); tab.State = TabState.Discarded; RefreshStatus(); }
        }
    }

    /// <summary>El proceso principal de WebView2 murió: todas las pestañas quedan descartadas y se crea un entorno nuevo.</summary>
    private async Task RecoverBrowserAsync()
    {
        if (_recovering) return;
        _recovering = true;
        try
        {
            foreach (var t in _tabs)
            {
                try { DisposeWebView(t); }
                catch (InvalidOperationException) { }
                t.State = TabState.Discarded;
            }
            var rtss = Process.GetProcessesByName("RTSS");
            if (rtss.Length > 0)
            {
                HintText.Text = "El motor se reinició: RivaTuner (RTSS) se inyecta en WebView2. Corré tools\\rtss-exclude.ps1 como administrador.";
                HintText.Visibility = Visibility.Visible;
                _log.Event("rtss_detected");
            }
            foreach (var p in rtss) p.Dispose();

            var now = DateTime.UtcNow;
            _recoveryTimes.Enqueue(now);
            while (_recoveryTimes.Peek() < now.AddMinutes(-2)) _recoveryTimes.Dequeue();
            _recoveries++;
            if (_recoveryTimes.Count > 3)
            {
                Placeholder.Text = "El motor del navegador se cayó varias veces. Revisá el log y reiniciá.";
                Placeholder.Visibility = Visibility.Visible;
                _log.Event("recovery_gave_up", ("recoveries", _recoveries));
                return;
            }
            _env = await CreateEnvironmentAsync();
            _log.Event("browser_recovered", ("recoveries", _recoveries));
            if (_active is { } a)
            {
                _active = null;
                await ActivateAsync(a);
            }
        }
        finally
        {
            _recovering = false;
        }
    }

    // ---------- Pestañas ----------

    private void AddTab(BrowserTab tab, int? index = null)
    {
        if (index is { } i && i <= _tabs.Count) _tabs.Insert(i, tab);
        else _tabs.Add(tab);
    }

    private async Task<BrowserTab> NewTabAsync(string url, bool navigate = true)
    {
        var tab = new BrowserTab { Url = url, Title = url == InternalPages.NewTab ? "Nueva pestaña" : url };
        AddTab(tab, _active is null ? null : _tabs.IndexOf(_active) + 1);
        await EnsureWebViewAsync(tab);
        if (navigate) Navigate(tab, url);
        return tab;
    }

    private async Task EnsureWebViewAsync(BrowserTab tab)
    {
        if (tab.WebView is not null || _env is null) return;
        var span = _log.StartSpan("webview_create", ("tab_id", tab.Id));
        var wv = new WebView2 { Visibility = Visibility.Collapsed };
        tab.WebView = wv;
        Host.Children.Add(wv);
        if (_isPrivate)
        {
            var options = _env.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            options.ProfileName = _app.PrivateProfileName;
            await wv.EnsureCoreWebView2Async(_env, options);
        }
        else
        {
            await wv.EnsureCoreWebView2Async(_env);
        }
        var core = wv.CoreWebView2;
        _app.BrowserProcessId = (int)core.BrowserProcessId;
        Harden(core);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(EditTrackerScript);

        core.SourceChanged += (_, _) =>
        {
            tab.Url = tab.InternalPage ?? core.Source;
            if (tab == _active) RefreshChrome();
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = core.DocumentTitle;
            if (!_isPrivate && tab.InternalPage is null) _app.Data.UpdateTitle(tab.Url, tab.Title);
            if (tab == _active) Title = $"{tab.Title} — {WindowSuffix}";
        };
        core.FaviconChanged += (_, _) => tab.FaviconUri = tab.InternalPage is null ? core.FaviconUri : null;
        core.IsDocumentPlayingAudioChanged += (_, _) => tab.PlayingAudio = core.IsDocumentPlayingAudio;
        core.NavigationStarting += (_, e) => OnNavigationStarting(tab, core, e);
        core.NavigationCompleted += (_, e) => OnNavigationCompleted(tab, e);
        core.NewWindowRequested += (_, e) => OnNewWindowRequested(tab, e);
        core.ProcessFailed += (_, e) => OnProcessFailed(tab, e);
        core.PermissionRequested += (_, e) => OnPermissionRequested(tab, e);
        core.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(tab == _active && core.ContainsFullScreenElement);
        wv.ZoomFactorChanged += (_, _) => { if (tab == _active) RefreshZoom(); };

        if (_settings.BlockingEnabled)
        {
            foreach (var filter in _blockList.UrlFilters())
                core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All,
                    CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) => OnResourceRequested(tab, e);
        }
        span.End();
    }

    /// <summary>Seguridad y privacidad por pestaña: lo que el navegador no usa, apagado.</summary>
    private void Harden(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreHostObjectsAllowed = false;      // ningún objeto .NET expuesto a las páginas
        s.IsWebMessageEnabled = false;        // sin canal página→app
        s.IsReputationCheckingRequired = true; // SmartScreen contra sitios y descargas maliciosas
        s.IsPasswordAutosaveEnabled = false;   // no guardar contraseñas en el perfil (usá un gestor)
        core.Profile.PreferredTrackingPreventionLevel = _settings.BlockingEnabled
            ? CoreWebView2TrackingPreventionLevel.Strict
            : CoreWebView2TrackingPreventionLevel.Balanced;
    }

    private void OnNavigationStarting(BrowserTab tab, CoreWebView2 core, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri;
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            tab.InternalPage ??= InternalPages.NewTab; // p. ej. volver atrás a la página de nueva pestaña
        }
        else
        {
            tab.InternalPage = null;
            if (_settings.HttpsFirst && HttpsUpgrade.Upgrade(uri, _httpExempt) is { } https)
            {
                var host = BrowserTab.HostOf(uri)!;
                if (tab.UpgradeHost == host)
                {
                    // Pasamos a https y el sitio nos redirigió de vuelta a http: no quiere https.
                    _httpExempt.Add(host);
                    tab.UpgradeHost = null;
                    _log.Event("https_fallback", ("host", host), ("reason", "redirect"));
                }
                else
                {
                    e.Cancel = true;
                    tab.CancelledNavigationId = e.NavigationId;
                    tab.CurrentNavigationId = e.NavigationId; // lo anterior a esto ya es viejo
                    tab.UpgradeHost = host;
                    tab.UpgradeFallback = uri;
                    _log.Event("https_upgrade", ("host", host));
                    Dispatcher.BeginInvoke(() => core.Navigate(https));
                    return;
                }
            }
        }
        tab.CurrentNavigationId = e.NavigationId;
        if (tab.RestoreSpan is null) tab.LastScroll = null; // página nueva: el scroll anterior ya no corresponde
        tab.TopHost = BrowserTab.HostOf(uri);
        tab.BlockedOnPage = 0;
        tab.IsLoading = true;
        if (tab == _active) RefreshChrome();
    }

    private void OnNavigationCompleted(BrowserTab tab, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.NavigationId == tab.CancelledNavigationId)
        {
            tab.CancelledNavigationId = null; // la http que cancelamos para ir a https: no es un error
            return;
        }
        if (tab.CurrentNavigationId is { } current && e.NavigationId < current)
            return; // navegación vieja interrumpida por una nueva (p. ej. la página de nueva pestaña)
        if (!e.IsSuccess && tab.InternalPage is not null)
            return; // las páginas propias no fallan: es la nueva pestaña interrumpida al escribir una dirección
        tab.IsLoading = false;

        if (tab.UpgradeHost is { } upgraded)
        {
            var fallback = tab.UpgradeFallback;
            tab.UpgradeHost = null;
            tab.UpgradeFallback = null;
            if (!e.IsSuccess && fallback is not null && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                _httpExempt.Add(upgraded);
                _log.Event("https_fallback", ("host", upgraded), ("reason", e.WebErrorStatus.ToString()));
                tab.Core?.Navigate(fallback);
                return;
            }
        }

        if (tab.SavedScroll is { } scroll)
        {
            tab.SavedScroll = null;
            _ = tab.Core?.ExecuteScriptAsync($"window.scrollTo(...{scroll})");
        }
        if (tab.RestoreSpan is { } span)
        {
            tab.RestoreSpan = null;
            span.End(e.IsSuccess);
        }
        if (tab.RevealPending) _ = RevealAfterRestoreAsync(tab);
        if (e.IsSuccess && tab == _active && tab.InternalPage is null) _ = SnapshotSoonAsync(tab, e.NavigationId);
        if (e.IsSuccess && tab.InternalPage is null && tab.Core is { } done)
            tab.Url = done.Source; // la dirección real (con redirecciones), no la que se escribió
        if (e.IsSuccess && !_isPrivate && tab.InternalPage is null)
            _app.Data.AddVisit(tab.Url, tab.Title, DateTime.UtcNow);
        _log.Event("navigation_completed", ("tab_id", tab.Id), ("ok", e.IsSuccess), ("host", BrowserTab.HostOf(tab.Url)),
            ("blocked", tab.BlockedOnPage), ("error", e.IsSuccess ? null : e.WebErrorStatus.ToString()));
        if (tab == _active) RefreshChrome();
    }


    private void OnPermissionRequested(BrowserTab tab, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.Notifications && _settings.BlockNotifications)
        {
            e.State = CoreWebView2PermissionState.Deny;
            _log.Event("permission_denied", ("kind", "notifications"), ("host", BrowserTab.HostOf(e.Uri)));
        }
        // El resto (cámara, micrófono, ubicación…) lo pregunta WebView2 con su diálogo.
    }

    private async void OnNewWindowRequested(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Ventanas nuevas (target=_blank, popups de login) como pestañas, conservando window.opener.
        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true;
            var tab = await NewTabAsync(e.Uri, navigate: false);
            e.NewWindow = tab.Core;
            await ActivateAsync(tab);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnResourceRequested(BrowserTab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        // Sólo llegan acá las peticiones que coinciden con algún filtro de la lista.
        var host = BrowserTab.HostOf(e.Request.Uri);
        if (host is null || !_blockList.IsBlocked(host) || IsSiteAllowed(tab.TopHost)) return;
        if (tab.TopHost is { } top && (host == top || host.EndsWith("." + top) || top.EndsWith("." + host))) return;
        e.Response = _env!.CreateWebResourceResponse(null, 403, "Blocked", "");
        tab.BlockedOnPage++;
        _blockedTotal++;
        if (tab == _active) RefreshShield();
    }

    private void Navigate(BrowserTab tab, string url)
    {
        var core = tab.Core;
        if (core is null) return;
        tab.Url = url;
        switch (url)
        {
            case InternalPages.NewTab:
            case InternalPages.History when _isPrivate: // el historial es de la ventana normal
                tab.Url = InternalPages.NewTab;
                tab.InternalPage = InternalPages.NewTab;
                tab.Title = "Nueva pestaña";
                core.NavigateToString(_isPrivate
                    ? InternalPages.BuildPrivateNewTab(_settings.SearchUrl)
                    : InternalPages.BuildNewTab(_app.Data, _settings.SearchUrl));
                return;
            case InternalPages.History:
                tab.InternalPage = url;
                tab.Title = "Historial";
                core.NavigateToString(InternalPages.BuildHistory(_app.Data));
                return;
        }
        tab.Title = url; // hasta que llegue el título real, como en otros navegadores
        try { core.Navigate(url); }
        catch (ArgumentException) { core.Navigate(_settings.SearchUrl + Uri.EscapeDataString(url)); }
    }

    private async Task ActivateAsync(BrowserTab tab)
    {
        var now = DateTime.UtcNow;
        if (_active is { } prev && prev != tab)
        {
            prev.LastActiveUtc = now;
            prev.IsActive = false;
            if (prev.Core is { } prevCore)
            {
                _ = SaveScrollAsync(prev, prevCore);
                prev.WebView!.Visibility = Visibility.Collapsed;
            }
            CloseFind();
            prev.RevealPending = false; // si estaba despertando, al volver se muestra directo
            HideSnapshot();
        }

        _active = tab;
        tab.IsActive = true;
        tab.LastActiveUtc = now;

        if (tab.State == TabState.Discarded)
        {
            tab.RestoreSpan = _log.StartSpan("restore_tab", ("tab_id", tab.Id), ("host", BrowserTab.HostOf(tab.Url)),
                ("snapshot", tab.SnapshotFile is not null));
            if (!ShowSnapshot(tab))
            {
                Placeholder.Text = $"Despertando {tab.Title}…";
                Placeholder.Visibility = Visibility.Visible;
            }
            var url = tab.Url;
            tab.SavedScroll = tab.LastScroll; // se reaplica al terminar de cargar
            await EnsureWebViewAsync(tab);
            tab.State = TabState.Live;
            Navigate(tab, url);
        }
        else if (tab.State == TabState.Suspended && tab.Core is { } core)
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            tab.State = TabState.Live;
            _log.Event("resume_tab", ("tab_id", tab.Id));
        }

        if (_active != tab) return; // el usuario cambió de pestaña mientras se restauraba
        Placeholder.Visibility = Visibility.Collapsed;
        if (tab.WebView is { } wv)
        {
            if (!tab.RevealPending) wv.Visibility = Visibility.Visible;
            if (tab.Url != InternalPages.NewTab) wv.Focus();
        }
        if (tab.Url == InternalPages.NewTab && !Address.IsKeyboardFocusWithin) FocusAddress();
        Title = $"{tab.Title} — {WindowSuffix}";
        if (tab.Url != InternalPages.NewTab) Address.Text = tab.Url; // al cambiar de pestaña, aunque la barra tenga el foco
        RefreshChrome();
        RefreshZoom();
        BringIntoView(tab);
    }

    private static async Task SaveScrollAsync(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            var json = await core.ExecuteScriptAsync("JSON.stringify([Math.round(scrollX), Math.round(scrollY)])");
            // ExecuteScriptAsync devuelve el string serializado otra vez como JSON.
            var value = System.Text.Json.JsonSerializer.Deserialize<string>(json);
            tab.LastScroll = value is "[0,0]" ? null : value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
        {
            // La pestaña se cerró o la página no permite scripts: no hay scroll que guardar.
        }
    }

    private async Task CloseTabAsync(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        if (!InternalPages.IsInternal(tab.Url)) _closed.Push(tab.Url);
        _tabs.RemoveAt(index);
        DisposeWebView(tab);
        DeleteSnapshot(tab);
        _log.Event("close_tab", ("tab_id", tab.Id));
        if (_tabs.Count == 0)
        {
            Close();
            return;
        }
        if (tab == _active)
        {
            _active = null;
            await ActivateAsync(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        RefreshStatus();
    }

    private void DisposeWebView(BrowserTab tab)
    {
        if (tab.WebView is not { } wv) return;
        if (_findCore is not null && _findCore == tab.Core) _findCore = null;
        tab.WebView = null;
        Host.Children.Remove(wv);
        wv.Dispose();
    }

    private async Task OpenOrActivateInternalAsync(string page)
    {
        var existing = _tabs.FirstOrDefault(t => t.Url == page);
        if (existing is not null)
        {
            await ActivateAsync(existing);
            Navigate(existing, page); // refresca el contenido
        }
        else
        {
            await ActivateAsync(await NewTabAsync(page));
        }
    }

    // ---------- Hibernación y presupuesto de RAM ----------

    private async void OnTick(object? sender, EventArgs e)
    {
        _ticks++;
        var sampled = _ticks % SampleEveryTicks == 0;
        if (sampled) Sample();
        if (_ticks % SaveEveryTicks == 0) SaveSession();
        if (_ticks % SnapshotEveryTicks == 0 && _active is { InternalPage: null, IsLoading: false } shown) _ = CaptureSnapshotAsync(shown);
        if (_applying) return;

        _applying = true;
        try
        {
            var decisions = HibernationPolicy.Decide(
                _tabs.Select(t => t.Snapshot()).ToList(), DateTime.UtcNow, _settings,
                _focusLostSinceUtc, sampled ? _lastMemory : null, _activeLowered, _lastBudgetDiscard);
            foreach (var d in decisions) await ApplyAsync(d);

            var focusTriggered = _focusLostSinceUtc is { } since
                && (DateTime.UtcNow - since).TotalSeconds >= _settings.FocusLostSeconds;
            if (_hibernateDonePending && focusTriggered)
            {
                _hibernateDonePending = false;
                _log.Event("hibernate_done", ("tabs", _tabs.Count),
                    ("sleeping", _tabs.Count(t => t.IsSleeping)), ("actions", decisions.Count));
            }
            if (decisions.Count > 0) RefreshStatus();
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task ApplyAsync(PolicyDecision d)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == d.TabId);
        if (tab?.Core is not { } core) return;
        switch (d.Action)
        {
            case HibernateAction.LowerMemory:
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                _activeLowered = true;
                _log.Event("lower_memory", ("tab_id", tab.Id), ("reason", d.Reason));
                break;

            case HibernateAction.Suspend:
            {
                var span = _log.StartSpan("suspend_tab", ("tab_id", tab.Id), ("reason", d.Reason));
                // Suspendida no contesta scripts y no se puede escribir en ella (no está a la vista): se mira ahora si
                // tiene algo escrito y eso vale hasta que vuelva.
                tab.UnsavedInputWhenSuspended = await HasUnsavedInputAsync(core);
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                var ok = false;
                try { ok = await core.TrySuspendAsync(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                // Aunque la suspensión falle (por ejemplo, un video en pausa), la memoria baja ya quedó aplicada.
                if (tab.State == TabState.Live && !tab.IsActive) tab.State = TabState.Suspended;
                span.End(ok);
                break;
            }

            case HibernateAction.Discard:
            {
                if (tab.IsActive) return;
                if (d.Reason is "idle" or "budget"
                    && (tab.State == TabState.Suspended ? tab.UnsavedInputWhenSuspended : await HasUnsavedInputAsync(core)))
                {
                    // Descartar borraría lo escrito. Se reintenta en 10 min; mientras, la política elige otra.
                    tab.KeepAliveUntilUtc = DateTime.UtcNow.AddMinutes(10);
                    _log.Event("discard_skipped", ("tab_id", tab.Id), ("reason", d.Reason), ("why", "unsaved_input"));
                    return;
                }
                var span = _log.StartSpan("discard_tab", ("tab_id", tab.Id), ("reason", d.Reason));
                DisposeWebView(tab);
                tab.State = TabState.Discarded;
                span.End();
                if (d.Reason == "budget")
                {
                    _lastBudgetDiscard = new BudgetDiscard(DateTime.UtcNow, _lastMemory?.TotalMb ?? 0);
                    _log.Event("budget_discard", ("tab_id", tab.Id), ("private_mb", _lastMemory?.TotalMb),
                        ("renderer_mb", _lastMemory?.RendererMb), ("budget_mb", _settings.RamBudgetMb));
                }
                break;
            }
        }
    }

    /// <summary>
    /// Marca los campos que la persona editó (eventos de entrada con isTrusted; lo que completa el sitio, como la
    /// búsqueda en el cuadro de Google o de Mercado Libre, no cuenta) y los olvida al enviar el formulario.
    /// </summary>
    private const string EditTrackerScript = """
        (() => {
          const key = Symbol.for('navegador.edited');
          if (window[key]) return;
          const edited = new Set();
          Object.defineProperty(window, key, { value: edited });
          document.addEventListener('input', e => {
            if (!e.isTrusted) return;
            const t = e.composedPath()[0];
            if (!(t instanceof Element)) return;
            const root = t.isContentEditable ? (t.closest('[contenteditable]:not([contenteditable=false])') || t) : t;
            edited.add(root);
          }, true);
          document.addEventListener('submit', e => {
            for (const el of e.target.elements || []) edited.delete(el);
          }, true);
        })();
        """;

    /// <summary>
    /// ¿La persona dejó algo escrito sin enviar? Campos que editó (ver EditTrackerScript) con texto distinto de su
    /// valor inicial, o un aviso de "salir de la página" por propiedad. Sólo el documento principal (los iframes de
    /// otro origen no se pueden leer). Si la página no contesta en 1,5 s se la trata como que sí: no se pierde nada por
    /// esperar.
    /// </summary>
    private static async Task<bool> HasUnsavedInputAsync(CoreWebView2 core)
    {
        const string script = """
            (() => {
              const edited = window[Symbol.for('navegador.edited')];
              for (const e of edited || []) {
                if (!e.isConnected || e.disabled || e.readOnly) { edited.delete(e); continue; }
                if (e.isContentEditable ? e.textContent.trim() : (e.value || '').trim() && e.value !== e.defaultValue) return true;
              }
              return typeof window.onbeforeunload === 'function';
            })()
            """;
        try
        {
            var run = core.ExecuteScriptAsync(script);
            if (await Task.WhenAny(run, Task.Delay(1500)) != run) return true;
            return await run == "true";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false; // el WebView ya no existe: no hay nada que perder
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_focusLostSinceUtc is not null) return;
        _focusLostSinceUtc = DateTime.UtcNow;
        _hibernateDonePending = true;
        _log.Event("focus_lost", ("foreground", ForegroundProcessName()));
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (_focusLostSinceUtc is null) return;
        _focusLostSinceUtc = null;
        _hibernateDonePending = false;
        _log.Event("focus_gained");
        if (_activeLowered && _active?.Core is { } core)
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        _activeLowered = false;
        _log.Event("tab_active_ready", ("tab_id", _active?.Id));
    }

    private static string? ForegroundProcessName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Memoria y CPU de todos los procesos del navegador: el propio y los de WebView2 de este entorno. La CPU es el
    /// porcentaje de la máquina (todos los núcleos = 100 %) entre esta muestra y la anterior.
    /// </summary>
    private void Sample()
    {
        if (_env is null) return;
        var pids = new HashSet<int> { Environment.ProcessId };
        var renderers = new HashSet<int>();
        try
        {
            foreach (var info in _env.GetProcessInfos())
            {
                pids.Add(info.ProcessId);
                if (info.Kind == CoreWebView2ProcessKind.Renderer) renderers.Add(info.ProcessId);
            }
        }
        catch (System.Runtime.InteropServices.COMException) { }

        long priv = 0, rss = 0, rendererPriv = 0;
        var alive = 0;
        var cpu = new Dictionary<int, TimeSpan>();
        TimeSpan cpuDelta = TimeSpan.Zero, rendererCpuDelta = TimeSpan.Zero;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                priv += p.PrivateMemorySize64;
                rss += p.WorkingSet64;
                if (renderers.Contains(pid)) rendererPriv += p.PrivateMemorySize64;
                cpu[pid] = p.TotalProcessorTime;
                if (_lastCpu.TryGetValue(pid, out var before))
                {
                    var delta = cpu[pid] - before;
                    cpuDelta += delta;
                    if (renderers.Contains(pid)) rendererCpuDelta += delta;
                }
                alive++;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        var now = DateTime.UtcNow;
        double? cpuPct = null, rendererCpuPct = null;
        if (_lastCpuAtUtc is { } at && (now - at).TotalSeconds > 0)
        {
            var capacity = (now - at).TotalMilliseconds * Environment.ProcessorCount;
            cpuPct = Math.Round(cpuDelta.TotalMilliseconds / capacity * 100, 2);
            rendererCpuPct = Math.Round(rendererCpuDelta.TotalMilliseconds / capacity * 100, 2);
        }
        _lastCpu = cpu;
        _lastCpuAtUtc = now;

        const double mb = 1024 * 1024;
        _lastPrivateMb = Math.Round(priv / mb, 1);
        _lastMemory = new MemoryReading(_lastPrivateMb.Value, Math.Round(rendererPriv / mb, 1));
        RefreshStatus();
        if (_isPrivate) return; // los procesos son los mismos que los de la ventana normal: no duplicar muestras
        _log.Write(new Dictionary<string, object?>
        {
            ["type"] = "sample",
            ["app_procs"] = alive,
            ["app_rss_mb"] = Math.Round(rss / mb, 1),
            ["app_private_mb"] = _lastPrivateMb,
            ["renderer_private_mb"] = _lastMemory.Value.RendererMb,
            ["cpu_pct"] = cpuPct,
            ["renderer_cpu_pct"] = rendererCpuPct,
            ["tabs"] = _tabs.Count,
            ["tabs_live"] = _tabs.Count(t => t.State == TabState.Live),
            ["tabs_suspended"] = _tabs.Count(t => t.State == TabState.Suspended),
            ["tabs_discarded"] = _tabs.Count(t => t.State == TabState.Discarded),
            ["blocked_total"] = _blockedTotal,
            ["focused"] = _focusLostSinceUtc is null,
        });
    }

    // ---------- Interfaz ----------

    private void RefreshChrome()
    {
        var core = _active?.Core;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        ReloadButton.Content = _active?.IsLoading == true ? "\uE711" : "\uE72C";
        var url = _active?.Url;
        if (!Address.IsKeyboardFocusWithin)
            Address.Text = url is InternalPages.NewTab or null ? "" : url;

        // Candado: https verde-gris, http con advertencia, páginas propias sin ícono.
        if (url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            LockIcon.Text = "\uE72E";
            LockIcon.Foreground = Brushes.Gray;
            LockIcon.ToolTip = "Conexión segura (HTTPS)";
        }
        else if (url is not null && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            LockIcon.Text = "\uE7BA";
            LockIcon.Foreground = Brushes.Orange;
            LockIcon.ToolTip = "Conexión no segura: lo que envíes puede verse en la red";
        }
        else
        {
            LockIcon.Text = "";
            LockIcon.ToolTip = null;
        }

        // La ventana privada no ve ni toca los favoritos.
        StarButton.Visibility = _isPrivate ? Visibility.Collapsed : Visibility.Visible;
        var bookmarked = !_isPrivate && url is not null && _app.Data.IsBookmarked(url);
        StarButton.Content = bookmarked ? "\uE735" : "\uE734";
        StarButton.Foreground = bookmarked ? Brushes.Gold : new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED));
        StarButton.IsEnabled = !_isPrivate && url is not null && BrowserData.IsRecordable(url);
        RefreshShield();
    }

    private void RefreshStatus()
    {
        var sleeping = _tabs.Count(t => t.IsSleeping);
        var ram = _lastPrivateMb is { } m ? $"{m:0} MB" : "…";
        MemoryText.Text = $"RAM {ram} de {_settings.RamBudgetMb} MB  ·  {_tabs.Count} pestañas  ·  {sleeping} dormidas";
        MemoryText.Foreground = _lastPrivateMb > _settings.RamBudgetMb
            ? Brushes.Orange
            : new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6));
    }

    private void RefreshShield()
    {
        if (!_settings.BlockingEnabled) { ShieldText.Text = "Bloqueo desactivado"; return; }
        var host = _active?.TopHost;
        ShieldText.Text = IsSiteAllowed(host)
            ? $"\U0001F6E1 Permitido en {host}  ·  {_blockedTotal} bloqueados en total"
            : $"\U0001F6E1 {_active?.BlockedOnPage ?? 0} bloqueados en este sitio  ·  {_blockedTotal} en total";
    }

    private void RefreshZoom()
    {
        var z = _active?.WebView?.ZoomFactor ?? 1.0;
        ZoomButton.Visibility = Math.Abs(z - 1.0) < 0.001 ? Visibility.Collapsed : Visibility.Visible;
        ZoomText.Text = $"\U0001F50D {z * 100:0} %";
    }

    private void Zoom(int direction)
    {
        if (_active?.WebView is not { } wv) return;
        if (direction == 0) { wv.ZoomFactor = 1.0; RefreshZoom(); return; }
        var current = wv.ZoomFactor;
        var next = direction > 0
            ? ZoomSteps.FirstOrDefault(s => s > current + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(s => s < current - 0.001, ZoomSteps[0]);
        wv.ZoomFactor = next;
        RefreshZoom();
    }

    private void SetFullscreen(bool on)
    {
        if (on && _beforeFullscreen is null)
        {
            _beforeFullscreen = (WindowState, WindowStyle);
            TabBar.Visibility = Toolbar.Visibility = StatusBar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else if (!on && _beforeFullscreen is { } b)
        {
            _beforeFullscreen = null;
            TabBar.Visibility = Toolbar.Visibility = StatusBar.Visibility = Visibility.Visible;
            WindowStyle = b.Style;
            WindowState = b.State;
        }
    }

    private void FocusAddress()
    {
        Address.Focus();
        Address.SelectAll();
    }

    private void BringIntoView(BrowserTab tab)
    {
        if (TabList.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement fe) fe.BringIntoView();
    }

    private async Task CycleTabAsync(int delta)
    {
        if (_active is null || _tabs.Count < 2) return;
        var i = (_tabs.IndexOf(_active) + delta + _tabs.Count) % _tabs.Count;
        await ActivateAsync(_tabs[i]);
    }

    // ---------- Buscar en la página ----------

    private void OpenFind()
    {
        if (_active?.Core is null || _active.InternalPage is not null) return;
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0) _ = StartFindAsync();
    }

    private void CloseFind()
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        FindBar.Visibility = Visibility.Collapsed;
        try { _findCore?.Find.Stop(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        _findCore = null;
        FindCount.Text = "";
        _active?.WebView?.Focus();
    }

    private async Task StartFindAsync()
    {
        if (_active?.Core is not { } core || _env is null) return;
        if (FindBox.Text.Length == 0)
        {
            try { core.Find.Stop(); } catch (InvalidOperationException) { }
            FindCount.Text = "";
            return;
        }
        if (_findCore != core)
        {
            _findCore = core;
            core.Find.MatchCountChanged += (_, _) => UpdateFindCount(core);
            core.Find.ActiveMatchIndexChanged += (_, _) => UpdateFindCount(core);
        }
        var options = _env.CreateFindOptions();
        options.FindTerm = FindBox.Text;
        options.SuppressDefaultFindDialog = true;
        options.ShouldHighlightAllMatches = true;
        try { await core.Find.StartAsync(options); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        UpdateFindCount(core);
    }

    private void UpdateFindCount(CoreWebView2 core)
    {
        if (core != _findCore) return;
        var count = core.Find.MatchCount;
        FindCount.Text = FindBox.Text.Length == 0 ? "" : count == 0 ? "Sin resultados" : $"{core.Find.ActiveMatchIndex + 1} de {count}";
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => _ = StartFindAsync();

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (NativeMethods.CurrentModifiers().HasFlag(ModifierKeys.Shift)) _findCore?.Find.FindPrevious();
            else _findCore?.Find.FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    private void FindPrev_Click(object sender, RoutedEventArgs e) => _findCore?.Find.FindPrevious();
    private void FindNext_Click(object sender, RoutedEventArgs e) => _findCore?.Find.FindNext();
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    // ---------- Acciones ----------

    private void ToggleBookmark()
    {
        if (_isPrivate || _active is not { } tab || !BrowserData.IsRecordable(tab.Url)) return;
        var added = _app.Data.ToggleBookmark(tab.Url, tab.Title);
        _app.SaveData();
        _log.Event(added ? "bookmark_added" : "bookmark_removed");
        RefreshChrome();
    }

    private async Task ClearBrowsingDataAsync(bool confirm = true)
    {
        if (confirm && MessageBox.Show(this,
                "Se borran el historial, las cookies (vas a tener que volver a iniciar sesión), la caché y los datos que guardan los sitios.\n\nLas pestañas abiertas y los favoritos quedan.",
                "Borrar datos de navegación", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            != MessageBoxResult.OK) return;
        var core = _active?.Core ?? _tabs.Select(t => t.Core).FirstOrDefault(c => c is not null);
        if (core is not null) await core.Profile.ClearBrowsingDataAsync();
        _app.Data.ClearHistory();
        foreach (var t in _tabs) t.SnapshotFile = null;
        try { if (Directory.Exists(_app.Paths.Snapshots)) Directory.Delete(_app.Paths.Snapshots, recursive: true); }
        catch (IOException) { }
        _app.SaveData();
        _log.Event("browsing_data_cleared");
        foreach (var t in _tabs.Where(t => t.InternalPage is not null && t.Core is not null)) Navigate(t, t.Url);
        _active?.Core?.Reload();
    }

    private void OpenSettings() =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_app.Paths.Settings}\"") { UseShellExecute = false });

    /// <summary>Registra el navegador (HKCU) y abre Configuración en su página: ahí se elige como predeterminado.</summary>
    private void MakeDefaultBrowser()
    {
        DefaultBrowser.Register(Environment.ProcessPath!);
        _log.Event("default_browser_registered");
        Process.Start(new ProcessStartInfo(DefaultBrowser.SettingsUri) { UseShellExecute = true });
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = NativeMethods.CurrentModifiers();
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var core = _active?.Core;

        // Se decide de forma sincrónica: e.Handled tiene que quedar puesto antes del primer await
        // para que WebView2 no procese también la tecla.
        Func<Task>? action = null;
        if (ctrl && key == Key.T && !shift) action = OpenBlankTabAsync;
        else if (ctrl && shift && key == Key.T && _closed.TryPop(out var url)) action = async () => await ActivateAsync(await NewTabAsync(url));
        else if (ctrl && shift && key == Key.N) _app.OpenWindow(null, isPrivate: true);
        else if (ctrl && key == Key.W && _active is { } current) action = () => CloseTabAsync(current);
        else if ((ctrl && key == Key.L) || (alt && key == Key.D) || key == Key.F6) FocusAddress();
        else if (ctrl && key == Key.D && !shift && !_isPrivate) ToggleBookmark();
        else if (ctrl && key == Key.H && !_isPrivate) action = () => OpenOrActivateInternalAsync(InternalPages.History);
        else if (ctrl && key == Key.J) core?.OpenDefaultDownloadDialog();
        else if (ctrl && key == Key.F) OpenFind();
        else if (ctrl && key == Key.P) core?.ShowPrintUI();
        else if (ctrl && shift && key == Key.Delete && !_isPrivate) action = () => ClearBrowsingDataAsync();
        else if (ctrl && key is Key.OemPlus or Key.Add) Zoom(+1);
        else if (ctrl && key is Key.OemMinus or Key.Subtract) Zoom(-1);
        else if (ctrl && key is Key.D0 or Key.NumPad0) Zoom(0);
        else if (ctrl && key == Key.Tab) action = () => CycleTabAsync(shift ? -1 : 1);
        else if (ctrl && key >= Key.D1 && key <= Key.D8 && key - Key.D1 < _tabs.Count) { var t = _tabs[key - Key.D1]; action = () => ActivateAsync(t); }
        else if (ctrl && key == Key.D9 && _tabs.Count > 0) { var t = _tabs[^1]; action = () => ActivateAsync(t); }
        else if (key == Key.F5 || (ctrl && key == Key.R)) core?.Reload();
        else if (alt && key == Key.Left && core?.CanGoBack == true) core.GoBack();
        else if (alt && key == Key.Right && core?.CanGoForward == true) core.GoForward();
        else if (key == Key.F11) SetFullscreen(_beforeFullscreen is null);
        else if (key == Key.Escape && _beforeFullscreen is not null && core?.ContainsFullScreenElement != true) SetFullscreen(false);
        else return;

        e.Handled = true;
        if (action is not null) await action();
    }

    /// <summary>La barra de direcciones toma el foco antes de crear el WebView: lo que se tipee enseguida no se pierde.</summary>
    private async Task OpenBlankTabAsync()
    {
        Address.Text = "";
        Address.Focus();
        await ActivateAsync(await NewTabAsync(InternalPages.NewTab));
    }

    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _active is not null)
        {
            var url = UrlInput.Resolve(Address.Text, _settings.SearchUrl);
            Navigate(_active, url);
            _active.WebView?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Address.Text = _active?.Url is InternalPages.NewTab or null ? "" : _active.Url;
            _active?.WebView?.Focus();
            e.Handled = true;
        }
    }

    private void Address_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => Address.SelectAll();

    private void Address_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Address.IsKeyboardFocusWithin) return;
        Address.Focus();
        e.Handled = true;
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e) => await OpenBlankTabAsync();

    private async void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is BrowserTab tab) await ActivateAsync(tab);
    }

    private async void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ((FrameworkElement)sender).DataContext is BrowserTab tab)
            await CloseTabAsync(tab);
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is BrowserTab tab) await CloseTabAsync(tab);
    }

    private static BrowserTab? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as BrowserTab;

    private async void TabMenu_Reload(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } tab) return;
        if (tab.Core is { } core) core.Reload();
        else await ActivateAsync(tab);
    }

    private async void TabMenu_Duplicate(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) await ActivateAsync(await NewTabAsync(tab.Url));
    }

    private async void TabMenu_Sleep(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { CanSleep: true } tab)
        {
            await ApplyAsync(new PolicyDecision(tab.Id, HibernateAction.Discard, "manual"));
            RefreshStatus();
        }
    }

    private async void TabMenu_CloseOthers(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } keep) return;
        foreach (var t in _tabs.Where(t => t != keep).ToList()) await CloseTabAsync(t);
        if (_active != keep) await ActivateAsync(keep);
    }

    private async void TabMenu_Close(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) await CloseTabAsync(tab);
    }

    private void TabScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _active?.Core?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _active?.Core?.GoForward();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        var core = _active?.Core;
        if (_active?.IsLoading == true) core?.Stop();
        else core?.Reload();
    }

    private void Star_Click(object sender, RoutedEventArgs e) => ToggleBookmark();

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => Zoom(0);

    private void Shield_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.TopHost is not { } host || !_settings.BlockingEnabled) return;
        bool allowed;
        if (_isPrivate)
        {
            allowed = !_privateAllowed.Remove(host);
            if (allowed) _privateAllowed.Add(host);
        }
        else
        {
            allowed = _settings.ToggleAllowed(host);
            _settings.Save(_app.Paths.Settings);
        }
        _log.Event(allowed ? "site_allowed" : "site_blocked", ("host", host));
        _active.Core?.Reload();
        RefreshShield();
    }

    private bool IsSiteAllowed(string? host) =>
        _isPrivate ? host is not null && _privateAllowed.Contains(host) : _settings.IsAllowed(host);

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, string? gesture, Action onClick, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            mi.Click += (_, _) => onClick();
            return mi;
        }
        var core = _active?.Core;
        var isSite = core is not null && _active!.InternalPage is null;

        menu.Items.Add(Item("Nueva pestaña", "Ctrl+T", () => _ = OpenBlankTabAsync()));
        menu.Items.Add(Item("Nueva ventana privada", "Ctrl+Shift+N", () => _app.OpenWindow(null, isPrivate: true)));
        menu.Items.Add(new Separator());

        if (!_isPrivate) // favoritos e historial son de la ventana normal
        {
            var favorites = new MenuItem { Header = "Favoritos" };
            var url = _active?.Url;
            var canBookmark = url is not null && BrowserData.IsRecordable(url);
            favorites.Items.Add(Item(url is not null && _app.Data.IsBookmarked(url) ? "Quitar esta página" : "Agregar esta página",
                "Ctrl+D", ToggleBookmark, canBookmark));
            if (_app.Data.Bookmarks.Count > 0) favorites.Items.Add(new Separator());
            foreach (var b in _app.Data.Bookmarks)
            {
                var target = b.Url;
                favorites.Items.Add(Item(b.Title.Length > 60 ? b.Title[..60] + "…" : b.Title, null, () => { if (_active is { } t) Navigate(t, target); }));
            }
            menu.Items.Add(favorites);
            menu.Items.Add(Item("Historial", "Ctrl+H", () => _ = OpenOrActivateInternalAsync(InternalPages.History)));
        }
        menu.Items.Add(Item("Descargas", "Ctrl+J", () => core?.OpenDefaultDownloadDialog(), core is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Acercar", "Ctrl++", () => Zoom(+1), core is not null));
        menu.Items.Add(Item("Alejar", "Ctrl+-", () => Zoom(-1), core is not null));
        menu.Items.Add(Item("Tamaño normal", "Ctrl+0", () => Zoom(0), core is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Buscar en la página", "Ctrl+F", OpenFind, isSite));
        menu.Items.Add(Item("Imprimir", "Ctrl+P", () => core?.ShowPrintUI(), isSite));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Borrar datos de navegación…", "Ctrl+Shift+Supr", () => _ = ClearBrowsingDataAsync(), !_isPrivate));
        menu.Items.Add(Item("Ajustes (se aplican al reiniciar)", null, OpenSettings));
        menu.Items.Add(Item("Abrir carpeta de datos y registros", null,
            () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_app.Paths.Root}\"") { UseShellExecute = false })));
        if (!_isPrivate)
            menu.Items.Add(Item("Hacer navegador predeterminado…", null, MakeDefaultBrowser));
        if (_app.AutomationScript is not null)
        {
            // Prueba automática: el menú no se abre (Windows lo traería al borde de la pantalla); se dibuja en la captura.
            _openMenu = menu;
            _menuAnchor = MenuButton;
            return;
        }
        menu.IsOpen = true;
    }
}
