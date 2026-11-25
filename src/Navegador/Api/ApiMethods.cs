using System.Text.Json;
using System.Windows;
using Navegador.Core;

namespace Navegador.Api;

/// <summary>
/// Pedidos con respuesta de la API (fase 6.3): {"type":"call","id":N,"method":"…","params":{…}} →
/// {"type":"result","id":N,"result":…} o {"type":"result","id":N,"error":"…"}. Cada método exige un permiso. Las
/// ventanas privadas no existen para la API: no se listan, no se leen y no se controlan.
/// </summary>
internal static class ApiMethods
{
    public const string TabsRead = "tabs.read";
    public const string TabsControl = "tabs.control";
    public const string MeetingControl = "meeting.control";
    private const int MaxPageChars = 100_000;

    private static readonly Dictionary<string, string> Required = new()
    {
        ["tabs.list"] = TabsRead,
        ["page.text"] = TabsRead,
        ["tabs.open"] = TabsControl,
        ["tabs.activate"] = TabsControl,
        ["meeting.set"] = MeetingControl,
    };

    public readonly record struct Reply(object? Result, string? Error);

    public static async Task<Reply> CallAsync(IReadOnlyCollection<string> scopes, string method, JsonElement args)
    {
        if (!Required.TryGetValue(method, out var scope)) return new(null, $"método desconocido: {method}");
        if (!scopes.Contains(scope)) return new(null, $"falta el permiso {scope}");
        try
        {
            var op = Application.Current.Dispatcher.InvokeAsync(() => RunAsync(method, args));
            return await await op.Task;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new(null, ex.Message);
        }
    }

    private static IEnumerable<(MainWindow Window, BrowserTab Tab)> NormalTabs() =>
        Application.Current.Windows.OfType<MainWindow>().Where(w => !w.IsPrivate).SelectMany(w => w.Tabs.Select(t => (w, t)));

    private static (MainWindow Window, BrowserTab Tab) FindTab(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("tab", out var t) && t.ValueKind == JsonValueKind.Number)
        {
            var id = t.GetInt32();
            foreach (var x in NormalTabs()) if (x.Tab.Id == id) return x;
            throw new ArgumentException($"no hay una pestaña {id} (las de ventanas privadas no se ven)");
        }
        // Sin pestaña: la activa de la ventana normal que se usó por última vez.
        var w = Application.Current.Windows.OfType<MainWindow>().Where(m => !m.IsPrivate)
                    .OrderByDescending(m => m.IsActive).FirstOrDefault()
                ?? throw new InvalidOperationException("no hay una ventana normal abierta");
        return (w, w.ActiveTab ?? throw new InvalidOperationException("no hay pestaña activa"));
    }

    private static async Task<Reply> RunAsync(string method, JsonElement args)
    {
        switch (method)
        {
            case "tabs.list":
                return new(NormalTabs().Select(x => new
                {
                    id = x.Tab.Id, title = x.Tab.Title, url = x.Tab.Url, active = x.Tab.IsActive,
                    state = x.Tab.State.ToString(), meeting = x.Tab.InMeeting,
                }).ToList(), null);

            case "page.text":
            {
                var (w, tab) = FindTab(args);
                if (tab.InternalPage is not null) return new(null, "es una página propia del navegador, no un sitio");
                if (tab.State != TabState.Live || tab.Core is null)
                    return new(null, "la pestaña está dormida: no se despierta desde la API (activala con tabs.activate)");
                var text = await w.ReadPageTextAsync(tab);
                if (text is null) return new(null, "la página no contestó");
                var truncated = text.Length > MaxPageChars;
                return new(new { tab = tab.Id, title = tab.Title, url = tab.Url, text = truncated ? text[..MaxPageChars] : text, truncated }, null);
            }

            case "tabs.open":
            {
                var url = args.GetProperty("url").GetString() ?? "";
                // Sólo sitios web: nada de file:, javascript:, data: ni páginas propias.
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                    return new(null, "sólo direcciones http:// o https://");
                var (w, _) = FindTab(default);
                var tab = await w.OpenForApiAsync(u.AbsoluteUri);
                return new(new { tab = tab.Id }, null);
            }

            case "tabs.activate":
            {
                var (w, tab) = FindTab(args);
                await w.ActivateForApiAsync(tab);
                return new(new { tab = tab.Id }, null);
            }

            case "meeting.set":
            {
                var (w, tab) = FindTab(args);
                var on = args.GetProperty("on").GetBoolean();
                if (on && tab.InternalPage is not null) return new(null, "el modo reunión es para sitios, no para páginas propias");
                await w.SetMeetingForApiAsync(tab, on);
                return new(new { tab = tab.Id, meeting = tab.InMeeting, reloaded = true }, null);
            }
        }
        return new(null, $"método desconocido: {method}");
    }
}
