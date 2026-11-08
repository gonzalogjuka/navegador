using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private readonly AppPaths _paths;
    private readonly Settings _settings;
    private readonly BlockList _blockList;
    private readonly EventLog _log;
    private readonly string? _startUrl;
    private readonly ObservableCollection<BrowserTab> _tabs = [];
    private readonly Stack<string> _closed = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private CoreWebView2Environment? _env;
    private BrowserTab? _active;
    private long _ticks;
    private bool _applying;
    private long _blockedTotal;
    private double? _lastPrivateMb;

    private DateTime? _focusLostSinceUtc;
    private bool _activeLowered;
    private bool _hibernateDonePending;

    private (WindowState State, WindowStyle Style)? _beforeFullscreen;

    public MainWindow(AppPaths paths, Settings settings, BlockList blockList, EventLog log, string? startUrl)
    {
        _paths = paths;
        _settings = settings;
        _blockList = blockList;
        _log = log;
        _startUrl = startUrl;
        InitializeComponent();
        TabList.ItemsSource = _tabs;
        Loaded += OnLoaded;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        _tick.Tick += OnTick;
    }

    // ---------- Arranque y cierre ----------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var span = _log.StartSpan("startup");
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

        var session = SessionStore.Load(_paths.Session);
        if (session is not null)
        {
            // Todas descartadas: arrancar no carga páginas que no vas a mirar.
            foreach (var t in session.Tabs)
                AddTab(new BrowserTab { Url = t.Url, Title = t.Title, State = TabState.Discarded });
        }

        if (_startUrl is not null)
            await ActivateAsync(await NewTabAsync(UrlInput.Resolve(_startUrl, _settings.SearchUrl)));
        else if (session is not null)
            await ActivateAsync(_tabs[session.ActiveIndex]);
        else
            await ActivateAsync(await NewTabAsync("about:blank"));

        span.End();
        _log.Event("startup_done", ("tabs", _tabs.Count), ("blocklist_domains", _blockList.Domains.Count));
        _tick.Start();
        Sample();
    }

    private Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        CoreWebView2Environment.CreateAsync(null, _paths.WebViewData,
            new CoreWebView2EnvironmentOptions(_settings.BrowserArguments));

    private int _recoveries;
    private readonly Queue<DateTime> _recoveryTimes = new();
    private bool _recovering;

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
            if (Process.GetProcessesByName("RTSS").Length > 0)
            {
                HintText.Text = "El motor se reinició: RivaTuner (RTSS) se inyecta en WebView2. Corré tools\\rtss-exclude.ps1 como administrador.";
                HintText.Visibility = Visibility.Visible;
                _log.Event("rtss_detected");
            }
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

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _tick.Stop();
        SaveSession();
        _log.Event("shutdown", ("tabs", _tabs.Count), ("blocked_total", _blockedTotal));
        foreach (var t in _tabs) t.WebView?.Dispose();
    }

    private void SaveSession()
    {
        if (_tabs.Count == 0) return;
        var tabs = _tabs.Select(t => new SavedTab(t.Url, t.Title)).ToList();
        var active = _active is null ? 0 : Math.Max(0, _tabs.IndexOf(_active));
        try { SessionStore.Save(_paths.Session, new SavedSession(tabs, active)); }
        catch (IOException ex) { _log.Event("error", ("where", "save_session"), ("message", ex.Message)); }
    }

    // ---------- Pestañas ----------

    private void AddTab(BrowserTab tab, int? index = null)
    {
        if (index is { } i && i <= _tabs.Count) _tabs.Insert(i, tab);
        else _tabs.Add(tab);
    }

    private async Task<BrowserTab> NewTabAsync(string url, bool navigate = true)
    {
        var tab = new BrowserTab { Url = url, Title = url == "about:blank" ? "Nueva pestaña" : url };
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
        await wv.EnsureCoreWebView2Async(_env);
        var core = wv.CoreWebView2;
        core.Profile.PreferredTrackingPreventionLevel = _settings.BlockingEnabled
            ? CoreWebView2TrackingPreventionLevel.Strict
            : CoreWebView2TrackingPreventionLevel.Balanced;

        core.SourceChanged += (_, _) =>
        {
            tab.Url = core.Source;
            if (tab == _active) RefreshChrome();
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = core.DocumentTitle;
            if (tab == _active) Title = $"{tab.Title} — Navegador";
        };
        core.FaviconChanged += (_, _) => tab.FaviconUri = core.FaviconUri;
        core.IsDocumentPlayingAudioChanged += (_, _) => tab.PlayingAudio = core.IsDocumentPlayingAudio;
        core.NavigationStarting += (_, e) =>
        {
            tab.TopHost = BrowserTab.HostOf(e.Uri);
            tab.BlockedOnPage = 0;
            tab.IsLoading = true;
            if (tab == _active) RefreshChrome();
        };
        core.NavigationCompleted += (_, e) => OnNavigationCompleted(tab, e);
        core.NewWindowRequested += (_, e) => OnNewWindowRequested(tab, e);
        core.ProcessFailed += (_, e) => OnProcessFailed(tab, e);
        core.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(tab == _active && core.ContainsFullScreenElement);

        if (_settings.BlockingEnabled)
        {
            foreach (var filter in _blockList.UrlFilters())
                core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All,
                    CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) => OnResourceRequested(tab, e);
        }
        span.End();
    }

    private void OnNavigationCompleted(BrowserTab tab, CoreWebView2NavigationCompletedEventArgs e)
    {
        tab.IsLoading = false;
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
        _log.Event("navigation_completed", ("tab_id", tab.Id), ("ok", e.IsSuccess), ("host", BrowserTab.HostOf(tab.Url)),
            ("blocked", tab.BlockedOnPage));
        if (tab == _active) RefreshChrome();
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
        if (host is null || !_blockList.IsBlocked(host) || _settings.IsAllowed(tab.TopHost)) return;
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
        }

        _active = tab;
        tab.IsActive = true;
        tab.LastActiveUtc = now;

        if (tab.State == TabState.Discarded)
        {
            Placeholder.Text = $"Despertando {tab.Title}…";
            Placeholder.Visibility = Visibility.Visible;
            tab.RestoreSpan = _log.StartSpan("restore_tab", ("tab_id", tab.Id), ("host", BrowserTab.HostOf(tab.Url)));
            var url = tab.Url;
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
            wv.Visibility = Visibility.Visible;
            if (tab.Url != "about:blank") wv.Focus();
        }
        if (tab.Url == "about:blank" && !Address.IsKeyboardFocusWithin) FocusAddress();
        Title = $"{tab.Title} — Navegador";
        RefreshChrome();
        BringIntoView(tab);
    }

    private static async Task SaveScrollAsync(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            var json = await core.ExecuteScriptAsync("JSON.stringify([Math.round(scrollX), Math.round(scrollY)])");
            // ExecuteScriptAsync devuelve el string serializado otra vez como JSON.
            var value = System.Text.Json.JsonSerializer.Deserialize<string>(json);
            tab.SavedScroll = value is "[0,0]" ? null : value;
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
        if (tab.Url != "about:blank") _closed.Push(tab.Url);
        _tabs.RemoveAt(index);
        DisposeWebView(tab);
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
        tab.WebView = null;
        Host.Children.Remove(wv);
        wv.Dispose();
    }

    // ---------- Hibernación y presupuesto de RAM ----------

    private async void OnTick(object? sender, EventArgs e)
    {
        _ticks++;
        var sampled = _ticks % SampleEveryTicks == 0;
        if (sampled) Sample();
        if (_ticks % SaveEveryTicks == 0) SaveSession();
        if (_applying) return;

        _applying = true;
        try
        {
            var decisions = HibernationPolicy.Decide(
                _tabs.Select(t => t.Snapshot()).ToList(), DateTime.UtcNow, _settings,
                _focusLostSinceUtc, sampled ? _lastPrivateMb : null, _activeLowered);
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
                var span = _log.StartSpan("discard_tab", ("tab_id", tab.Id), ("reason", d.Reason));
                DisposeWebView(tab);
                tab.State = TabState.Discarded;
                span.End();
                if (d.Reason == "budget")
                    _log.Event("budget_discard", ("tab_id", tab.Id), ("private_mb", _lastPrivateMb), ("budget_mb", _settings.RamBudgetMb));
                break;
            }
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

    /// <summary>Memoria de todos los procesos del navegador: el propio y los de WebView2 de este entorno.</summary>
    private void Sample()
    {
        if (_env is null) return;
        var pids = new HashSet<int> { Environment.ProcessId };
        try
        {
            foreach (var info in _env.GetProcessInfos()) pids.Add(info.ProcessId);
        }
        catch (System.Runtime.InteropServices.COMException) { }

        long priv = 0, rss = 0;
        var alive = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                priv += p.PrivateMemorySize64;
                rss += p.WorkingSet64;
                alive++;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        const double mb = 1024 * 1024;
        _lastPrivateMb = Math.Round(priv / mb, 1);
        _log.Write(new Dictionary<string, object?>
        {
            ["type"] = "sample",
            ["app_procs"] = alive,
            ["app_rss_mb"] = Math.Round(rss / mb, 1),
            ["app_private_mb"] = _lastPrivateMb,
            ["tabs"] = _tabs.Count,
            ["tabs_live"] = _tabs.Count(t => t.State == TabState.Live),
            ["tabs_suspended"] = _tabs.Count(t => t.State == TabState.Suspended),
            ["tabs_discarded"] = _tabs.Count(t => t.State == TabState.Discarded),
            ["blocked_total"] = _blockedTotal,
            ["focused"] = _focusLostSinceUtc is null,
        });
        RefreshStatus();
    }

    // ---------- Interfaz ----------

    private void RefreshChrome()
    {
        var core = _active?.Core;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        ReloadButton.Content = _active?.IsLoading == true ? "\uE711" : "\uE72C";
        if (!Address.IsKeyboardFocusWithin)
            Address.Text = _active?.Url is "about:blank" or null ? "" : _active.Url;
        RefreshShield();
    }

    private void RefreshStatus()
    {
        var sleeping = _tabs.Count(t => t.IsSleeping);
        var ram = _lastPrivateMb is { } m ? $"{m:0} MB" : "…";
        MemoryText.Text = $"RAM {ram} de {_settings.RamBudgetMb} MB  ·  {_tabs.Count} pestañas  ·  {sleeping} dormidas";
        MemoryText.Foreground = _lastPrivateMb > _settings.RamBudgetMb
            ? System.Windows.Media.Brushes.Orange
            : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#9AA0A6")!;
    }

    private void RefreshShield()
    {
        if (!_settings.BlockingEnabled) { ShieldText.Text = "Bloqueo desactivado"; return; }
        var host = _active?.TopHost;
        ShieldText.Text = _settings.IsAllowed(host)
            ? $"\U0001F6E1 Permitido en {host}  ·  {_blockedTotal} bloqueados en total"
            : $"\U0001F6E1 {_active?.BlockedOnPage ?? 0} bloqueados en este sitio  ·  {_blockedTotal} en total";
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
        else if (ctrl && key == Key.W && _active is { } current) action = () => CloseTabAsync(current);
        else if ((ctrl && key == Key.L) || (alt && key == Key.D) || key == Key.F6) FocusAddress();
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
        await ActivateAsync(await NewTabAsync("about:blank"));
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
            Address.Text = _active?.Url is "about:blank" or null ? "" : _active.Url;
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

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_paths.Settings}\"") { UseShellExecute = false });

    private void Shield_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.TopHost is not { } host || !_settings.BlockingEnabled) return;
        var allowed = _settings.ToggleAllowed(host);
        _settings.Save(_paths.Settings);
        _log.Event(allowed ? "site_allowed" : "site_blocked", ("host", host));
        _active.Core?.Reload();
        RefreshShield();
    }
}
