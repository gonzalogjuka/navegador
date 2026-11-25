using System.IO;
using System.Windows;
using System.Windows.Threading;
using Navegador.Core;

namespace Navegador;

public partial class App : Application
{
    private AppServices? _services;
    private SingleInstance? _instance;

    /// <summary>
    /// Uso: Navegador.exe [--data-dir carpeta] [--private] [--automation pasos.txt] [url | archivo.html]
    ///      Navegador.exe --register-default | --unregister-default   (registro como navegador en HKCU y sale)
    /// </summary>
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var paths = AppPaths.Default();
        string? url = null;
        var isPrivate = false;
        string? automation = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            var arg = e.Args[i];
            if (arg == "--data-dir" && i + 1 < e.Args.Length) paths = new AppPaths(Path.GetFullPath(e.Args[++i]));
            else if (arg == "--private") isPrivate = true;
            else if (arg == "--automation" && i + 1 < e.Args.Length) automation = Path.GetFullPath(e.Args[++i]);
            else if (arg is "--register-default" or "--unregister-default")
            {
                if (arg == "--register-default") DefaultBrowser.Register(Environment.ProcessPath!);
                else DefaultBrowser.Unregister();
                Shutdown();
                return;
            }
            else url ??= Path.IsPathRooted(arg) && File.Exists(arg) ? new Uri(arg).AbsoluteUri : arg; // .html abierto desde el Explorador
        }

        try
        {
            _instance = SingleInstance.TryAcquire(paths.Root, new SingleInstance.Request(url, isPrivate));
        }
        catch (TimeoutException)
        {
            MessageBox.Show("Navegador ya está abierto pero no responde. Cerralo desde el Administrador de tareas y volvé a abrirlo.",
                "Navegador", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }
        if (_instance is null) { Shutdown(); return; } // el pedido lo atiende la instancia que ya estaba abierta

        var settings = Settings.LoadOrCreate(paths.Settings);
        var blockFile = Path.Combine(AppContext.BaseDirectory, "blocklist.txt");
        var blockList = File.Exists(blockFile) ? BlockList.Parse(File.ReadAllText(blockFile)) : new BlockList([]);
        EventLog OpenLog()
        {
            var log = new EventLog(Path.Combine(paths.Logs, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));
            log.Event("record_start", ("app", "navegador"), ("version", typeof(App).Assembly.GetName().Version?.ToString()),
                ("ram_budget_mb", settings.RamBudgetMb), ("suspend_after_s", settings.SuspendAfterSeconds),
                ("discard_after_min", settings.DiscardAfterMinutes), ("focus_lost_s", settings.FocusLostSeconds),
                ("https_first", settings.HttpsFirst), ("block_notifications", settings.BlockNotifications));
            return log;
        }

        _services = new AppServices(paths, settings, blockList, OpenLog, BrowserData.Load(paths.Data)) { AutomationScript = automation };
        if (automation is not null) _services.EnsureLog(); // la prueba siempre deja log (también arrancando en privada)
        _services.DeletePrivateProfiles();
        var services = _services;
        _instance.Listen(request => Dispatcher.InvokeAsync(() => services.HandleRequest(request)));
        if (settings.ApiEnabled)
        {
            services.EnsureLog(); // conexiones y permisos de apps quedan en el log
            services.Api = new Api.ApiServer(services) { Approve = services.AskApprovalAsync };
            services.Api.Start();
        }
        _services.OpenWindow(url, isPrivate);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Event("error", ("where", "unhandled"), ("exception", e.Exception.GetType().Name), ("message", e.Exception.Message),
            ("stack", e.Exception.StackTrace));
        // Un error de una pestaña no debería tirar abajo todo el navegador; queda en el log para arreglarlo.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services?.NormalWindowOpened == true) _services.SaveData();
        _services?.Api?.Dispose();
        _services?.DeletePrivateProfilesOnExit();
        _services?.Log.Event("record_stop");
        _services?.Log.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
