using System.IO;
using System.Windows;
using System.Windows.Threading;
using Navegador.Core;

namespace Navegador;

public partial class App : Application
{
    private EventLog? _log;
    private AppServices? _services;

    /// <summary>Uso: Navegador.exe [--data-dir carpeta] [--private] [--automation pasos.txt] [url]</summary>
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var paths = AppPaths.Default();
        string? url = null;
        var isPrivate = false;
        string? automation = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--data-dir" && i + 1 < e.Args.Length) paths = new AppPaths(Path.GetFullPath(e.Args[++i]));
            else if (e.Args[i] == "--private") isPrivate = true;
            else if (e.Args[i] == "--automation" && i + 1 < e.Args.Length) automation = Path.GetFullPath(e.Args[++i]);
            else url ??= e.Args[i];
        }

        // Arrancar directo en privada (--private) no deja log: ni siquiera el archivo de la sesión.
        _log = isPrivate && automation is null
            ? EventLog.Off
            : new EventLog(Path.Combine(paths.Logs, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));
        var settings = Settings.LoadOrCreate(paths.Settings);
        var blockFile = Path.Combine(AppContext.BaseDirectory, "blocklist.txt");
        var blockList = File.Exists(blockFile) ? BlockList.Parse(File.ReadAllText(blockFile)) : new BlockList([]);
        _log.Event("record_start", ("app", "navegador"), ("version", typeof(App).Assembly.GetName().Version?.ToString()),
            ("ram_budget_mb", settings.RamBudgetMb), ("suspend_after_s", settings.SuspendAfterSeconds),
            ("discard_after_min", settings.DiscardAfterMinutes), ("focus_lost_s", settings.FocusLostSeconds),
            ("https_first", settings.HttpsFirst), ("block_notifications", settings.BlockNotifications));

        _services = new AppServices(paths, settings, blockList, _log, BrowserData.Load(paths.Data)) { AutomationScript = automation };
        _services.DeletePrivateProfiles();
        _services.OpenWindow(url, isPrivate);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Event("error", ("where", "unhandled"), ("exception", e.Exception.GetType().Name), ("message", e.Exception.Message),
            ("stack", e.Exception.StackTrace));
        // Un error de una pestaña no debería tirar abajo todo el navegador; queda en el log para arreglarlo.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services?.NormalWindowOpened == true) _services.SaveData();
        _services?.DeletePrivateProfilesOnExit();
        _log?.Event("record_stop");
        _log?.Dispose();
        base.OnExit(e);
    }
}
