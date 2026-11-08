using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Navegador.Core;

namespace Navegador;

/// <summary>
/// Pruebas de humo sin teclas simuladas: Navegador.exe --automation pasos.txt ejecuta los pasos dentro de la app,
/// así no depende del foco ni puede escribir en otra ventana. Una orden por línea; '#' comenta.
///   wait MS · go TEXTO · newtab [TEXTO] · tab N · sleep N · bookmark · find TEXTO · findclose · zoom +|-|0
///   history · menu · tabmenu N · closemenus · private · main · shot NOMBRE · close
///   js CÓDIGO (resultado al log) · cleardata (borra datos sin preguntar)
/// Las capturas (interfaz + página + menú abierto, aunque la ventana esté tapada) van a &lt;datos&gt;\shots\NOMBRE.png.
/// </summary>
public partial class MainWindow
{
    private ContextMenu? _openMenu;
    /// <summary>Elemento debajo del cual se dibuja el menú en la captura (botón ⋮ o pestaña).</summary>
    private FrameworkElement? _menuAnchor;

    /// <summary>
    /// Ejecuta los pasos y al final cierra la app siempre (aunque falte el archivo, falle un paso o no haya "close"):
    /// en modo prueba las ventanas están fuera de pantalla y una que quedara abierta sería invisible.
    /// </summary>
    private async Task RunAutomationAsync(string path)
    {
        var target = this;
        try
        {
            foreach (var raw in await File.ReadAllLinesAsync(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var sp = line.IndexOf(' ');
                var cmd = sp < 0 ? line : line[..sp];
                var arg = sp < 0 ? "" : line[(sp + 1)..].Trim();
                _log.Event("automation_step", ("cmd", cmd), ("arg", arg), ("private", target._isPrivate));
                try
                {
                    await target.RunStepAsync(cmd, arg, w => target = w);
                }
                catch (Exception ex)
                {
                    _log.Event("automation_error", ("cmd", cmd), ("message", ex.Message));
                }
            }
        }
        catch (IOException ex)
        {
            _log.Event("automation_error", ("cmd", "(archivo)"), ("message", ex.Message));
        }
        _log.Event("automation_done");
        Application.Current.Shutdown();
    }

    private async Task RunStepAsync(string cmd, string arg, Action<MainWindow> switchTo)
    {
        switch (cmd)
        {
            case "wait": await Task.Delay(int.Parse(arg)); break;
            case "go": NavigateLikeEnter(arg); break;
            case "newtab":
                await OpenBlankTabAsync();
                if (arg.Length > 0) NavigateLikeEnter(arg);
                break;
            case "tab": await ActivateAsync(_tabs[int.Parse(arg)]); break;
            case "js":
                var result = await _active!.Core!.ExecuteScriptAsync(arg);
                _log.Event("automation_js", ("result", result));
                break;
            case "cleardata": await ClearBrowsingDataAsync(confirm: false); break;
            case "sleep":
                var t = _tabs[int.Parse(arg)];
                if (t.CanSleep) await ApplyAsync(new PolicyDecision(t.Id, HibernateAction.Discard, "manual"));
                RefreshStatus();
                break;
            case "bookmark": ToggleBookmark(); break;
            case "find": OpenFind(); FindBox.Text = arg; break;
            case "findclose": CloseFind(); break;
            case "zoom": Zoom(arg == "+" ? 1 : arg == "-" ? -1 : 0); break;
            case "history": await OpenOrActivateInternalAsync(InternalPages.History); break;
            case "menu": Menu_Click(MenuButton, new RoutedEventArgs()); break;
            case "tabmenu":
                if (TabList.ItemContainerGenerator.ContainerFromIndex(int.Parse(arg)) is DependencyObject c
                    && VisualTreeHelper.GetChildrenCount(c) > 0 && VisualTreeHelper.GetChild(c, 0) is FrameworkElement fe
                    && fe.ContextMenu is { } cm)
                {
                    // No se abre (un menú no puede quedar fuera de la pantalla): se dibuja en la próxima captura.
                    cm.DataContext = fe.DataContext;
                    _openMenu = cm;
                    _menuAnchor = fe;
                }
                break;
            case "closemenus":
                _openMenu = null;
                _menuAnchor = null;
                break;
            case "private":
                var w = _app.OpenWindow(null, isPrivate: true);
                for (var i = 0; i < 100 && w._active?.Core is null; i++) await Task.Delay(100);
                switchTo(w);
                break;
            case "main":
                switchTo(Application.Current.Windows.OfType<MainWindow>().First(m => !m._isPrivate));
                break;
            case "shot": await ShotAsync(arg); break;
            case "close": Close(); break;
            default: throw new ArgumentException($"orden desconocida: {cmd}");
        }
    }

    /// <summary>Lo mismo que escribir en la barra y apretar Enter: navega y pasa el foco a la página.</summary>
    private void NavigateLikeEnter(string text)
    {
        Navigate(_active!, UrlInput.Resolve(text, _settings.SearchUrl));
        _active!.WebView?.Focus();
    }

    /// <summary>
    /// Captura que no depende de lo que haya en pantalla: la interfaz se dibuja con WPF, el contenido de la página con
    /// CapturePreviewAsync de WebView2 y el menú abierto (si hay) encima, en su posición.
    /// </summary>
    private async Task ShotAsync(string name)
    {
        await Task.Delay(300);
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        int W(double v) => (int)Math.Ceiling(v * dpi.DpiScaleX);
        int H(double v) => (int)Math.Ceiling(v * dpi.DpiScaleY);

        var chrome = new RenderTargetBitmap(W(root.ActualWidth), H(root.ActualHeight), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        chrome.Render(root);

        BitmapSource? page = null;
        if (_active?.Core is { } core && _active.WebView?.Visibility == Visibility.Visible)
        {
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            page = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(chrome, new System.Windows.Rect(0, 0, root.ActualWidth, root.ActualHeight));
            if (page is not null)
            {
                var at = Host.TranslatePoint(new Point(0, 0), root);
                dc.DrawImage(page, new System.Windows.Rect(at.X, at.Y, Host.ActualWidth, Host.ActualHeight));
            }
            if (_openMenu is { } menu && _menuAnchor is { } anchor)
            {
                // El menú nunca se abrió: se le aplican los estilos de la ventana, se mide y se dibuja a mano.
                if (!menu.Resources.MergedDictionaries.Contains(Resources)) menu.Resources.MergedDictionaries.Add(Resources);
                menu.ApplyTemplate();
                menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                menu.Arrange(new System.Windows.Rect(menu.DesiredSize));
                menu.UpdateLayout();
                var size = menu.DesiredSize;
                var menuShot = new RenderTargetBitmap(W(size.Width), H(size.Height), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                menuShot.Render(menu);
                var at = anchor.TranslatePoint(new Point(0, anchor.ActualHeight), root);
                var x = anchor == MenuButton ? at.X + anchor.ActualWidth - size.Width : at.X;
                x = Math.Clamp(x, 0, Math.Max(0, root.ActualWidth - size.Width));
                dc.DrawImage(menuShot, new System.Windows.Rect(x, at.Y, size.Width, size.Height));
            }
        }
        var final = new RenderTargetBitmap(W(root.ActualWidth), H(root.ActualHeight), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        final.Render(dv);

        var dir = Path.Combine(_app.Paths.Root, "shots");
        Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));
        await using var fs = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(fs);
    }
}
