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
