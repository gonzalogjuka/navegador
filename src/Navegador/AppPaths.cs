using System.IO;
using System.Runtime.InteropServices;

namespace Navegador;

/// <summary>Dónde vive cada cosa. Por defecto %LOCALAPPDATA%\Navegador; --data-dir lo cambia (pruebas).</summary>
public sealed record AppPaths(string Root)
{
    public string WebViewData => Path.Combine(Root, "WebView2");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Session => Path.Combine(Root, "session.json");
    public string Data => Path.Combine(Root, "data.json");
    public string Snapshots => Path.Combine(Root, "snapshots");
    public string Logs => Path.Combine(Root, "logs");
    /// <summary>Apps locales autorizadas a usar la API (sólo hashes de sus tokens).</summary>
    public string Integrations => Path.Combine(Root, "integrations.json");
    /// <summary>Dirección de la API mientras el navegador está abierto (para que las apps la encuentren).</summary>
    public string ApiInfo => Path.Combine(Root, "api.json");

    /// <summary>
    /// Prefijo de los perfiles de WebView2 de las ventanas privadas. Sólo se usan en modo InPrivate, así que no heredan
    /// nada del perfil normal; cada tanda de ventanas privadas usa uno nuevo (ver AppServices.PrivateProfileName).
    /// </summary>
    public const string PrivateProfilePrefix = "Privada-";

    /// <summary>Carpetas de esos perfiles: WebView2 los crea en EBWebView\WV2Profile_&lt;nombre en minúsculas&gt;.</summary>
    public IEnumerable<string> PrivateProfileDirs()
    {
        var root = Path.Combine(WebViewData, "EBWebView");
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, "WV2Profile_" + PrivateProfilePrefix.ToLowerInvariant() + "*")
            : [];
    }

    public static AppPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Navegador"));
}

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    internal static partial short GetKeyState(int vKey);

    internal const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>Avisa al Explorador que cambiaron las asociaciones de archivos/protocolos (SHCNE_ASSOCCHANGED).</summary>
    [LibraryImport("shell32.dll")]
    internal static partial void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>
    /// Modificadores del teclado. Con el foco dentro de WebView2 el teclado lo procesa otro proceso y
    /// Keyboard.Modifiers (el estado propio de WPF) no ve Ctrl/Shift/Alt. WebView2 sincroniza el estado de
    /// teclas Win32 del hilo antes de avisar el atajo, así que GetKeyState sí refleja la combinación pulsada
    /// (GetAsyncKeyState no: lee el instante actual y el Ctrl ya puede estar suelto).
    /// </summary>
    internal static System.Windows.Input.ModifierKeys CurrentModifiers()
    {
        var m = System.Windows.Input.ModifierKeys.None;
        if (GetKeyState(0x11) < 0) m |= System.Windows.Input.ModifierKeys.Control;
        if (GetKeyState(0x10) < 0) m |= System.Windows.Input.ModifierKeys.Shift;
        if (GetKeyState(0x12) < 0) m |= System.Windows.Input.ModifierKeys.Alt;
        return m;
    }
}
