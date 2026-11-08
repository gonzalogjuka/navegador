using System.IO;
using Navegador.Core;
using Process = System.Diagnostics.Process;

namespace Navegador;

/// <summary>
/// Estado compartido por todas las ventanas (la normal y las privadas).
/// El log se abre recién con la primera ventana normal (o con --automation): una corrida sólo privada no deja archivo.
/// </summary>
public sealed class AppServices(AppPaths paths, Settings settings, BlockList blockList, Func<EventLog> openLog, BrowserData data)
{
    public AppPaths Paths { get; } = paths;
    public Settings Settings { get; } = settings;
    public BlockList BlockList { get; } = blockList;
    public EventLog Log { get; private set; } = EventLog.Off;

    public void EnsureLog()
    {
        if (Log == EventLog.Off) Log = openLog();
    }

    /// <summary>Favoritos e historial. Las ventanas privadas no los leen ni los escriben.</summary>
    public BrowserData Data { get; } = data;
    /// <summary>Hosts sin HTTPS detectados en esta corrida por las ventanas normales (HTTPS primero los deja en http).</summary>
    public HashSet<string> HttpExempt { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>--automation: archivo de pasos de prueba (ver MainWindow.Automation.cs y TakeAutomationScript).</summary>
    public string? AutomationScript { get; init; }
    /// <summary>Si se abrió alguna ventana normal. Con sólo ventanas privadas no se escribe data.json.</summary>
    public bool NormalWindowOpened { get; private set; }
    public bool PrivateWindowOpened { get; private set; }
    /// <summary>Proceso navegador de WebView2 (lo anota cada pestaña al crearse; cambia si el motor se reinicia).</summary>
    public int BrowserProcessId { get; set; }
    /// <summary>
    /// Perfil de WebView2 de las ventanas privadas abiertas. Al cerrarse la última se manda a borrar y la próxima tanda
    /// usa uno nuevo: WebView2 no deja volver a abrir un perfil que se está borrando (0x8007012F).
    /// </summary>
    public string PrivateProfileName { get; private set; } = NewPrivateProfileName();

    private bool _automationStarted;

    /// <summary>El archivo de pasos lo ejecuta una sola vez la primera ventana que carga (con --private, la privada).</summary>
    public string? TakeAutomationScript()
    {
        if (_automationStarted || AutomationScript is null) return null;
        _automationStarted = true;
        return AutomationScript;
    }

    public void SaveData()
    {
        try { Data.Save(Paths.Data); }
        catch (IOException ex) { Log.Event("error", ("where", "save_data"), ("message", ex.Message)); }
    }

    public void RetirePrivateProfile() => PrivateProfileName = NewPrivateProfileName();

    private static string NewPrivateProfileName() => AppPaths.PrivateProfilePrefix + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Borra las carpetas de perfiles privados que hayan quedado (el esqueleto que crea WebView2: preferencias y fechas
    /// de uso; la navegación InPrivate nunca llega a disco). Normalmente WebView2 ya las borró al cerrar la última ventana
    /// privada; esto cubre un cierre forzado. Se llama al arrancar, antes de crear ningún WebView2.
    /// </summary>
    public void DeletePrivateProfiles() => DeletePrivateProfiles(attempts: 1);

    /// <param name="attempts">Intentos por carpeta, cada 300 ms: al salir, algún proceso hijo de WebView2 puede soltar
    /// los archivos un poco después que el proceso principal.</param>
    private void DeletePrivateProfiles(int attempts)
    {
        foreach (var dir in Paths.PrivateProfileDirs().ToList())
        {
            for (var i = 1; ; i++)
            {
                try { Directory.Delete(dir, recursive: true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (i < attempts) { Thread.Sleep(300); continue; }
                    Log.Event("error", ("where", "delete_private_profile"), ("message", ex.Message), ("attempts", i));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Al salir: espera a que termine el proceso de WebView2 (suelta los archivos del perfil) y borra los perfiles
    /// privados en ese momento, sin depender de que WebView2 llegue a hacerlo ni del próximo arranque.
    /// </summary>
    public void DeletePrivateProfilesOnExit()
    {
        if (!PrivateWindowOpened) return;
        if (BrowserProcessId > 0)
        {
            try { using var p = Process.GetProcessById(BrowserProcessId); p.WaitForExit(10_000); }
            catch (ArgumentException) { } // ya terminó
        }
        DeletePrivateProfiles(attempts: 20);
    }

    public MainWindow OpenWindow(string? startUrl, bool isPrivate)
    {
        if (isPrivate) PrivateWindowOpened = true;
        else
        {
            NormalWindowOpened = true;
            EnsureLog();
        }
        var w = new MainWindow(this, startUrl, isPrivate);
        if (AutomationScript is not null)
        {
            // Prueba automática: la ventana existe y dibuja, pero fuera de la pantalla, sin barra de tareas
            // y sin tomar el foco, para no tapar ni interrumpir lo que esté haciendo el usuario.
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            w.Left = -32000;
            w.Top = -32000;
        }
        w.Show();
        return w;
    }

    /// <summary>
    /// Pedido de otro arranque (doble clic en el acceso directo, un link desde otra app): --private abre una ventana
    /// privada; si no, la dirección va a una pestaña nueva de la ventana normal (que se abre si no había).
    /// </summary>
    public void HandleRequest(SingleInstance.Request request)
    {
        Log.Event("instance_request", ("private", request.Private), ("has_url", request.Url is not null));
        if (request.Private) { OpenWindow(request.Url, isPrivate: true); return; }
        if (System.Windows.Application.Current.Windows.OfType<MainWindow>().LastOrDefault(m => !m.IsPrivate) is { } w)
            w.OpenFromOutside(request.Url, bringToFront: AutomationScript is null);
        else
            OpenWindow(request.Url, isPrivate: false);
    }
}
