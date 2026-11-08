using System.Net;

namespace Navegador.Core;

/// <summary>
/// HTTPS primero: una navegación a http:// se reintenta como https://. Si el sitio no tiene HTTPS, el host
/// queda exceptuado (con sus subdominios) y se vuelve a http://. No se tocan redes locales: localhost, IPs,
/// nombres sin punto (intranet), .local/.lan/.home.arpa ni direcciones con puerto explícito.
/// </summary>
public static class HttpsUpgrade
{
    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home.arpa", ".internal", ".localhost"];

    /// <summary>URL https equivalente, o null si no corresponde subirla.</summary>
    public static string? Upgrade(string url, ICollection<string> exemptHosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp) return null;
        var host = u.Host.ToLowerInvariant();
        if (!u.IsDefaultPort || IsExempt(host, exemptHosts)) return null;
        if (host == "localhost" || !host.Contains('.') || IPAddress.TryParse(host.Trim('[', ']'), out _)) return null;
        if (LocalSuffixes.Any(s => host.EndsWith(s))) return null;
        return new UriBuilder(u) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
    }

    /// <summary>Un host está exceptuado si él o un dominio padre lo está (neverssl.com cubre x.neverssl.com).</summary>
    private static bool IsExempt(string host, ICollection<string> exemptHosts)
    {
        for (var h = host; ;)
        {
            if (exemptHosts.Contains(h)) return true;
            var dot = h.IndexOf('.');
            if (dot < 0) return false;
            h = h[(dot + 1)..];
        }
    }
}
