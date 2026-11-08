namespace Navegador.Core;

/// <summary>Convierte lo que se escribe en la barra de direcciones en una URL navegable.</summary>
public static class UrlInput
{
    private static readonly string[] KnownSchemes = ["http", "https", "file", "about", "edge", "data", "view-source"];

    public static string Resolve(string input, string searchUrl)
    {
        var text = input.Trim();
        if (text.Length == 0) return "about:blank";

        var colon = text.IndexOf(':');
        if (colon > 0 && KnownSchemes.Contains(text[..colon].ToLowerInvariant()))
            return text;

        if (!text.Contains(' ') && LooksLikeHost(text))
            return "https://" + text;
        if (!text.Contains(' ') && IsLocalhost(text))
            return "http://" + text;

        return searchUrl + Uri.EscapeDataString(text);
    }

    private static bool IsLocalhost(string text)
    {
        var host = HostPart(text);
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";
    }

    private static bool LooksLikeHost(string text)
    {
        var host = HostPart(text);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1") return false;
        var dot = host.LastIndexOf('.');
        if (dot <= 0 || dot == host.Length - 1) return false;
        var tld = host[(dot + 1)..];
        return tld.All(char.IsAsciiLetter) && tld.Length >= 2
            || System.Net.IPAddress.TryParse(host, out _);
    }

    private static string HostPart(string text)
    {
        var end = text.IndexOfAny(['/', '?', '#']);
        var host = end >= 0 ? text[..end] : text;
        var port = host.LastIndexOf(':');
        return port > 0 ? host[..port] : host;
    }
}
