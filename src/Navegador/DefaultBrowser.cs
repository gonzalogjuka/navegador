using Microsoft.Win32;

namespace Navegador;

/// <summary>
/// Registro como navegador en HKCU (sin permisos de administrador), para que aparezca en Configuración → Aplicaciones
/// predeterminadas. Windows no deja que un programa se ponga predeterminado solo: el usuario lo elige en esa pantalla.
/// Registra http, https, .htm y .html con el ejecutable actual; volver a registrar actualiza la ruta.
/// </summary>
public static class DefaultBrowser
{
    public const string AppName = "Navegador";
    private const string ClientKey = @"Software\Clients\StartMenuInternet\" + AppName;
    private const string UrlProgId = "NavegadorURL";
    private const string HtmlProgId = "NavegadorHTML";

    /// <summary>Página de Configuración de este navegador (elegir para qué tipos es el predeterminado).</summary>
    public const string SettingsUri = "ms-settings:defaultapps?registeredAppUser=" + AppName;

    public static void Register(string exePath)
    {
        var open = $"\"{exePath}\" \"%1\"";
        var icon = $"\"{exePath}\",0";
        foreach (var (progId, description) in new[] { (UrlProgId, "Navegador URL"), (HtmlProgId, "Documento HTML (Navegador)") })
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId);
            k.SetValue("", description);
            k.SetValue("FriendlyTypeName", description);
            using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", icon);
            using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
        }

        using (var client = Registry.CurrentUser.CreateSubKey(ClientKey))
        {
            client.SetValue("", AppName);
            using (var i = client.CreateSubKey("DefaultIcon")) i.SetValue("", icon);
            using (var c = client.CreateSubKey(@"shell\open\command")) c.SetValue("", $"\"{exePath}\"");
            using var caps = client.CreateSubKey("Capabilities");
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "Navegador liviano: pestañas que se duermen, presupuesto de RAM y bloqueo de rastreo.");
            caps.SetValue("ApplicationIcon", icon);
            using (var s = caps.CreateSubKey("StartMenu")) s.SetValue("StartMenuInternet", AppName);
            using (var u = caps.CreateSubKey("URLAssociations"))
            {
                u.SetValue("http", UrlProgId);
                u.SetValue("https", UrlProgId);
            }
            using (var f = caps.CreateSubKey("FileAssociations"))
            {
                f.SetValue(".htm", HtmlProgId);
                f.SetValue(".html", HtmlProgId);
            }
        }

        using (var apps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            apps.SetValue(AppName, ClientKey + @"\Capabilities");
        NotifyShell();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + UrlProgId, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + HtmlProgId, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClientKey, throwOnMissingSubKey: false);
        using (var apps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            apps?.DeleteValue(AppName, throwOnMissingValue: false);
        NotifyShell();
    }

    private static void NotifyShell() => NativeMethods.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
