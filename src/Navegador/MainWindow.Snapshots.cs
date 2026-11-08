using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Navegador.Core;

namespace Navegador;

/// <summary>
/// Snapshots: foto JPEG de lo que se ve en la pestaña activa (2 s después de cargar y cada 20 s), guardada en disco.
/// Cuando una pestaña descartada despierta se muestra la foto al instante y la página real la reemplaza al terminar
/// de cargar y recuperar el scroll. La ventana privada nunca guarda snapshots.
/// </summary>
public partial class MainWindow
{
    private const int SnapshotEveryTicks = 20;
    private static readonly TimeSpan SnapshotAfterLoad = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RevealTimeout = TimeSpan.FromSeconds(8);

    private string SnapshotPath(string name) => Path.Combine(_app.Paths.Snapshots, name);

    private async Task SnapshotSoonAsync(BrowserTab tab, ulong navigationId)
    {
        await Task.Delay(SnapshotAfterLoad);
        if (tab == _active && tab.CurrentNavigationId == navigationId) await CaptureSnapshotAsync(tab);
    }

    /// <summary>Sólo la pestaña activa y visible: una oculta no se dibuja y la foto saldría en blanco.</summary>
    private async Task CaptureSnapshotAsync(BrowserTab tab)
    {
        if (_isPrivate || tab != _active || tab.RevealPending || tab.InternalPage is not null
            || tab.WebView?.Visibility != Visibility.Visible || tab.Core is not { } core) return;
        await SaveScrollAsync(tab, core); // la foto y el scroll guardado tienen que coincidir
        var name = tab.SnapshotFile ?? SnapshotFiles.NewName();
        var span = _log.StartSpan("snapshot_save", ("tab_id", tab.Id));
        try
        {
            Directory.CreateDirectory(_app.Paths.Snapshots);
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
            if (ms.Length == 0 || !_tabs.Contains(tab)) { span.End(false); return; }
            var tmp = SnapshotPath(name) + ".tmp";
            await File.WriteAllBytesAsync(tmp, ms.ToArray());
            File.Move(tmp, SnapshotPath(name), overwrite: true);
            tab.SnapshotFile = name;
            span.End();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            span.End(false);
        }
    }

    /// <summary>Muestra la foto de la pestaña que despierta. Devuelve false si no hay.</summary>
    private bool ShowSnapshot(BrowserTab tab)
    {
        if (tab.SnapshotFile is not { } name || !File.Exists(SnapshotPath(name))) return false;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;            // no deja el archivo bloqueado
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(SnapshotPath(name));
            img.EndInit();
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            SnapshotImage.Source = img;
            SnapshotImage.Width = img.PixelWidth / dpi.DpiScaleX;   // la foto está en píxeles reales
            SnapshotImage.Height = img.PixelHeight / dpi.DpiScaleY;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return false;
        }
        SnapshotOverlay.Visibility = Visibility.Visible;
        tab.RevealPending = true;
        _ = RevealOnTimeoutAsync(tab);
        return true;
    }

    private void HideSnapshot()
    {
        SnapshotOverlay.Visibility = Visibility.Collapsed;
        SnapshotImage.Source = null;
    }

    /// <summary>La página real terminó de cargar: se espera a que aplique el scroll y reemplaza a la foto.</summary>
    private async Task RevealAfterRestoreAsync(BrowserTab tab)
    {
        await Task.Delay(150);
        Reveal(tab);
    }

    private async Task RevealOnTimeoutAsync(BrowserTab tab)
    {
        await Task.Delay(RevealTimeout);
        if (tab.RevealPending) { _log.Event("snapshot_reveal_timeout", ("tab_id", tab.Id)); Reveal(tab); }
    }

    private void Reveal(BrowserTab tab)
    {
        if (!tab.RevealPending) return;
        tab.RevealPending = false;
        if (tab != _active) return;
        HideSnapshot();
        if (tab.WebView is { } wv)
        {
            wv.Visibility = Visibility.Visible;
            wv.Focus();
        }
    }

    private void DeleteSnapshot(BrowserTab tab)
    {
        if (tab.SnapshotFile is not { } name) return;
        tab.SnapshotFile = null;
        try { File.Delete(SnapshotPath(name)); }
        catch (IOException) { }
    }

    /// <summary>Al arrancar: borra fotos de pestañas que ya no existen.</summary>
    private void DeleteOrphanSnapshots()
    {
        if (!Directory.Exists(_app.Paths.Snapshots)) return;
        var files = Directory.EnumerateFiles(_app.Paths.Snapshots).Select(Path.GetFileName).OfType<string>();
        foreach (var orphan in SnapshotFiles.Orphans(files, _tabs.Select(t => t.SnapshotFile)).ToList())
        {
            try { File.Delete(SnapshotPath(orphan)); }
            catch (IOException) { }
        }
    }
}
