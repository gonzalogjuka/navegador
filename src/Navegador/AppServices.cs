using System.IO;
using Navegador.Core;

namespace Navegador;

/// <summary>Estado compartido por todas las ventanas (la normal y las privadas).</summary>
public sealed class AppServices(AppPaths paths, Settings settings, BlockList blockList, EventLog log, BrowserData data)
{
    public AppPaths Paths { get; } = paths;
    public Settings Settings { get; } = settings;
    public BlockList BlockList { get; } = blockList;
    public EventLog Log { get; } = log;
    /// <summary>Favoritos e historial. Las ventanas privadas leen favoritos pero no escriben historial.</summary>
    public BrowserData Data { get; } = data;
    /// <summary>Hosts sin HTTPS detectados en esta corrida (HTTPS primero los deja en http).</summary>
    public HashSet<string> HttpExempt { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>--automation: archivo de pasos de prueba que ejecuta la ventana principal (ver MainWindow.Automation.cs).</summary>
    public string? AutomationScript { get; init; }

    public void SaveData()
    {
        try { Data.Save(Paths.Data); }
        catch (IOException ex) { Log.Event("error", ("where", "save_data"), ("message", ex.Message)); }
    }

    public MainWindow OpenWindow(string? startUrl, bool isPrivate)
    {
        var w = new MainWindow(this, startUrl, isPrivate);
        w.Show();
        return w;
    }
}
