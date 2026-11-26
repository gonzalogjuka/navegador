using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Navegador;

/// <summary>
/// Permisos del sitio (clic en el candado): ver y cambiar micrófono, cámara, ubicación y lo que el sitio tenga guardado.
/// WebView2 recuerda "Bloquear" para siempre y sin esto no había forma de deshacerlo: Meet quedaba sin micrófono y su
/// botón "Usar el micrófono" no hacía nada. Si el sitio tiene bloqueado el micrófono o la cámara, el candado lo avisa.
/// </summary>
public partial class MainWindow
{
    /// <summary>Siempre en el menú; los demás permisos aparecen sólo si el sitio tiene algo guardado.</summary>
    private static readonly (CoreWebView2PermissionKind Kind, string Label)[] MainPermissions =
    [
        (CoreWebView2PermissionKind.Microphone, "Micrófono"),
        (CoreWebView2PermissionKind.Camera, "Cámara"),
        (CoreWebView2PermissionKind.Geolocation, "Ubicación"),
    ];

    private static readonly Dictionary<CoreWebView2PermissionKind, string> OtherPermissionLabels = new()
    {
        [CoreWebView2PermissionKind.Notifications] = "Notificaciones",
        [CoreWebView2PermissionKind.ClipboardRead] = "Leer el portapapeles",
        [CoreWebView2PermissionKind.MultipleAutomaticDownloads] = "Varias descargas automáticas",
        [CoreWebView2PermissionKind.FileReadWrite] = "Archivos",
        [CoreWebView2PermissionKind.Autoplay] = "Reproducción automática",
        [CoreWebView2PermissionKind.LocalFonts] = "Fuentes del equipo",
        [CoreWebView2PermissionKind.MidiSystemExclusiveMessages] = "Dispositivos MIDI",
        [CoreWebView2PermissionKind.WindowManagement] = "Ventanas en varias pantallas",
    };

    /// <summary>Origen de la pestaña activa con el micrófono o la cámara bloqueados (para el aviso del candado).</summary>
    private string? _mediaBlockedOrigin;

    private static string? OriginOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.GetLeftPart(UriPartial.Authority).ToLowerInvariant()
            : null;

    private static async Task<Dictionary<CoreWebView2PermissionKind, CoreWebView2PermissionState>> SitePermissionsAsync(
        CoreWebView2 core, string origin)
    {
        var all = await core.Profile.GetNonDefaultPermissionSettingsAsync();
        var result = new Dictionary<CoreWebView2PermissionKind, CoreWebView2PermissionState>();
        foreach (var s in all)
            if (OriginOf(s.PermissionOrigin) == origin) result[s.PermissionKind] = s.PermissionState;
        return result;
    }

    /// <summary>Vuelve a mirar si la pestaña activa tiene el micrófono o la cámara bloqueados y actualiza el candado.</summary>
    private async Task RefreshPermissionHintAsync()
    {
        var tab = _active;
        string? blocked = null;
        if (tab?.Core is { } core && tab.InternalPage is null && OriginOf(tab.Url) is { } origin)
        {
            try
            {
                var perms = await SitePermissionsAsync(core, origin);
                if (perms.TryGetValue(CoreWebView2PermissionKind.Microphone, out var mic) && mic == CoreWebView2PermissionState.Deny
                    || perms.TryGetValue(CoreWebView2PermissionKind.Camera, out var cam) && cam == CoreWebView2PermissionState.Deny)
                    blocked = origin;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return; // la pestaña se cerró mientras se consultaba
            }
        }
        if (tab != _active || blocked == _mediaBlockedOrigin) return;
        _mediaBlockedOrigin = blocked;
        RefreshChrome();
    }

    /// <summary>Aviso en el candado (lo llama RefreshChrome después de dibujar el candado normal).</summary>
    private void ApplyPermissionHint(string? url)
    {
        if (LockIcon.Text.Length == 0) return; // páginas propias: sin permisos que mostrar
        if (_mediaBlockedOrigin is not null && _mediaBlockedOrigin == OriginOf(url))
        {
            LockIcon.Text = "\uEC54"; // micrófono tachado
            LockIcon.Foreground = Brushes.Orange;
            LockIcon.ToolTip = "Bloqueaste el micrófono o la cámara en este sitio. Clic para cambiarlo.";
        }
        else
        {
            LockIcon.ToolTip = $"{LockIcon.ToolTip}. Clic: permisos del sitio";
        }
    }

    private async void Lock_Click(object sender, MouseButtonEventArgs e)
    {
        if (_active is not { Core: { } core, InternalPage: null } tab || OriginOf(tab.Url) is not { } origin) return;
        Dictionary<CoreWebView2PermissionKind, CoreWebView2PermissionState> perms;
        try { perms = await SitePermissionsAsync(core, origin); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return; }
        if (tab != _active) return;

        var menu = new ContextMenu { PlacementTarget = LockIcon, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = $"Permisos de {new Uri(origin).Host}", IsEnabled = false });
        menu.Items.Add(new Separator());
        var kinds = MainPermissions.ToList();
        foreach (var kind in perms.Keys)
            if (kinds.All(k => k.Kind != kind))
                kinds.Add((kind, OtherPermissionLabels.TryGetValue(kind, out var label) ? label : kind.ToString()));
        // Sin submenús: el submenú de WPF se dibuja claro y con el texto claro del tema no se leía nada (26/11).
        foreach (var (kind, label) in kinds)
        {
            var current = perms.TryGetValue(kind, out var s) ? s : CoreWebView2PermissionState.Default;
            menu.Items.Add(new MenuItem { Header = label, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false, Focusable = false });
            foreach (var state in new[] { CoreWebView2PermissionState.Default, CoreWebView2PermissionState.Allow, CoreWebView2PermissionState.Deny })
            {
                var chosen = state == current;
                var option = new MenuItem { Header = (chosen ? "✓  " : "     ") + StateLabel(state) };
                if (chosen)
                {
                    option.FontWeight = FontWeights.Bold;
                    option.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8));
                }
                else option.Click += async (_, _) => await SetSitePermissionAsync(tab, origin, kind, state);
                menu.Items.Add(option);
            }
        }
        if (perms.Count > 0)
        {
            menu.Items.Add(new Separator());
            var reset = new MenuItem { Header = "Restablecer los permisos de este sitio" };
            reset.Click += async (_, _) =>
            {
                foreach (var kind in perms.Keys) await SetSitePermissionAsync(tab, origin, kind, CoreWebView2PermissionState.Default, reload: false);
                tab.Core?.Reload();
            };
            menu.Items.Add(reset);
        }
        if (_app.AutomationScript is not null)
        {
            // Prueba automática: el menú no se abre (quedaría en el borde de la pantalla); se dibuja en la captura.
            _openMenu = menu;
            _menuAnchor = LockIcon;
            return;
        }
        menu.IsOpen = true;
    }

    private static string StateLabel(CoreWebView2PermissionState state) => state switch
    {
        CoreWebView2PermissionState.Allow => "Permitir",
        CoreWebView2PermissionState.Deny => "Bloquear",
        _ => "Preguntar",
    };

    /// <summary>Guarda el permiso y recarga la página: los sitios leen el permiso al cargar (Meet no vuelve a preguntar).</summary>
    private async Task SetSitePermissionAsync(BrowserTab tab, string origin, CoreWebView2PermissionKind kind,
        CoreWebView2PermissionState state, bool reload = true)
    {
        if (tab.Core is not { } core) return;
        try
        {
            await core.Profile.SetPermissionStateAsync(kind, origin, state);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _log.Event("error", ("where", "set_permission"), ("message", ex.Message));
            return;
        }
        _log.Event("site_permission", ("host", new Uri(origin).Host), ("kind", kind.ToString()), ("state", state.ToString()));
        if (reload) core.Reload();
        await RefreshPermissionHintAsync();
    }
}
