using System.Text.Json;

namespace Navegador;

/// <summary>Lo que la API (Api/ApiMethods.cs) puede pedirle a una ventana. Siempre en el hilo de la interfaz.</summary>
public partial class MainWindow
{
    internal IReadOnlyList<BrowserTab> Tabs => _tabs;
    internal BrowserTab? ActiveTab => _active;

    /// <summary>Texto visible de la página (innerText del documento principal), o null si no contesta en 3 s.</summary>
    internal async Task<string?> ReadPageTextAsync(BrowserTab tab)
    {
        if (tab.Core is not { } core) return null;
        var script = core.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
        if (await Task.WhenAny(script, Task.Delay(3000)) != script) return null;
        try { return JsonSerializer.Deserialize<string>(await script); }
        catch (JsonException) { return null; }
    }

    internal async Task<BrowserTab> OpenForApiAsync(string url)
    {
        var tab = await NewTabAsync(url);
        await ActivateAsync(tab);
        _log.Event("api_open", ("tab_id", tab.Id), ("host", BrowserTab.HostOf(url)));
        return tab;
    }

    internal Task ActivateForApiAsync(BrowserTab tab) => ActivateAsync(tab);

    internal Task SetMeetingForApiAsync(BrowserTab tab, bool on) => SetMeetingAsync(tab, on);
}
