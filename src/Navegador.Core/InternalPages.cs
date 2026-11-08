using System.Net;
using System.Text;

namespace Navegador.Core;

/// <summary>
/// Páginas propias del navegador (about:blank = nueva pestaña, about:history = historial). Se generan como HTML
/// sin JavaScript y con una CSP que sólo permite estilos en línea y formularios: los títulos y URLs vienen de
/// sitios ajenos y se escapan siempre; sólo se enlazan URLs http/https.
/// </summary>
public static class InternalPages
{
    public const string NewTab = "about:blank";
    public const string History = "about:history";

    public static bool IsInternal(string url) => url is NewTab or History;

    private const string Head = """
        <!doctype html><html lang="es"><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; form-action https: http:">
        <title>{0}</title><style>
        body{{margin:0;background:#202124;color:#e8eaed;font:15px 'Segoe UI',sans-serif}}
        main{{max-width:760px;margin:0 auto;padding:12vh 24px 48px}}
        h1{{font-weight:300;font-size:28px;margin:0 0 24px}} h2{{font-size:13px;font-weight:600;color:#9aa0a6;text-transform:uppercase;margin:36px 0 12px}}
        form{{display:flex}} input{{flex:1;background:#303134;color:#e8eaed;border:1px solid #5f6368;border-radius:24px;padding:12px 20px;font:16px 'Segoe UI';outline:none}}
        input:focus{{border-color:#8ab4f8}}
        .grid{{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:10px}}
        .tile{{display:block;background:#303134;border-radius:10px;padding:12px;color:#e8eaed;text-decoration:none;overflow:hidden;white-space:nowrap;text-overflow:ellipsis}}
        .tile:hover,.row:hover{{background:#3c4043}} .tile b{{display:inline-block;width:24px;height:24px;line-height:24px;text-align:center;border-radius:6px;background:#8ab4f8;color:#202124;margin-right:8px}}
        .row{{display:flex;gap:12px;padding:8px 10px;border-radius:8px;color:#e8eaed;text-decoration:none}}
        .row span{{overflow:hidden;white-space:nowrap;text-overflow:ellipsis}} .t{{color:#9aa0a6;min-width:90px}} .h{{color:#9aa0a6;margin-left:auto;padding-left:12px}}
        .muted{{color:#9aa0a6}}
        </style></head><body><main>
        """;

    public static string BuildNewTab(BrowserData data, string searchUrl, bool isPrivate)
    {
        var sb = new StringBuilder();
        sb.AppendFormat(Head, "Nueva pestaña");
        sb.Append(isPrivate ? "<h1>Ventana privada</h1><p class=\"muted\">No se guarda historial, cookies ni sesión al cerrar.</p>" : "<h1>Navegador</h1>");
        AppendSearch(sb, searchUrl);

        if (data.Bookmarks.Count > 0)
        {
            sb.Append("<h2>Favoritos</h2><div class=\"grid\">");
            foreach (var b in data.Bookmarks)
                if (SafeHref(b.Url) is { } href)
                    sb.Append($"<a class=\"tile\" href=\"{href}\" title=\"{Enc(b.Url)}\"><b>{Enc(Initial(b.Title, b.Url))}</b>{Enc(b.Title)}</a>");
            sb.Append("</div>");
        }

        if (!isPrivate)
        {
            var recent = data.Recent(8).ToList();
            if (recent.Count > 0)
            {
                sb.Append("<h2>Recientes</h2>");
                foreach (var h in recent) AppendRow(sb, h, h.LastVisitUtc.ToLocalTime().ToString("HH:mm"));
            }
        }
        return sb.Append("</main></body></html>").ToString();
    }

    public static string BuildHistory(BrowserData data, int max = 500)
    {
        var sb = new StringBuilder();
        sb.AppendFormat(Head, "Historial");
        sb.Append("<h1>Historial</h1><p class=\"muted\">Se guarda sólo en esta PC. Para borrarlo: menú ⋮ → Borrar datos de navegación.</p>");
        string? day = null;
        foreach (var h in data.Recent(max))
        {
            var local = h.LastVisitUtc.ToLocalTime();
            var d = local.ToString("dddd d 'de' MMMM", new System.Globalization.CultureInfo("es-AR"));
            if (d != day) { sb.Append($"<h2>{Enc(d)}</h2>"); day = d; }
            AppendRow(sb, h, local.ToString("HH:mm"));
        }
        if (day is null) sb.Append("<p class=\"muted\">Todavía no hay nada.</p>");
        return sb.Append("</main></body></html>").ToString();
    }

    private static void AppendSearch(StringBuilder sb, string searchUrl)
    {
        // "https://www.google.com/search?q=" → action "https://www.google.com/search", campo "q".
        var q = searchUrl.IndexOf('?');
        var action = q >= 0 ? searchUrl[..q] : searchUrl;
        var hidden = new StringBuilder();
        var field = "q";
        if (q >= 0)
        {
            var pairs = searchUrl[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < pairs.Length; i++)
            {
                var kv = pairs[i].Split('=', 2);
                var value = kv.Length > 1 ? WebUtility.UrlDecode(kv[1]) : "";
                if (i == pairs.Length - 1 && value == "") field = kv[0];
                else hidden.Append($"<input type=\"hidden\" name=\"{Enc(kv[0])}\" value=\"{Enc(value)}\">");
            }
        }
        if (SafeHref(action) is not { } safeAction) return;
        sb.Append($"<form action=\"{safeAction}\" method=\"get\">{hidden}<input name=\"{Enc(field)}\" placeholder=\"Buscar o escribir una dirección arriba\" autofocus autocomplete=\"off\"></form>");
    }

    private static void AppendRow(StringBuilder sb, HistoryEntry h, string time)
    {
        if (SafeHref(h.Url) is not { } href) return;
        var title = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title;
        var host = Uri.TryCreate(h.Url, UriKind.Absolute, out var u) ? u.Host : "";
        sb.Append($"<a class=\"row\" href=\"{href}\"><span class=\"t\">{Enc(time)}</span><span>{Enc(title)}</span><span class=\"h\">{Enc(host)}</span></a>");
    }

    /// <summary>Sólo http/https, escapado para atributo. Cualquier otra cosa (javascript:, data:) no se enlaza.</summary>
    private static string? SafeHref(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? Enc(u.AbsoluteUri)
            : null;

    private static string Initial(string title, string url)
    {
        var s = string.IsNullOrWhiteSpace(title) ? url : title;
        return s.Trim()[..1].ToUpperInvariant();
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
