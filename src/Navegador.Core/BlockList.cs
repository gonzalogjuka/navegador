namespace Navegador.Core;

/// <summary>
/// Lista de dominios bloqueados. Un dominio bloquea también todos sus subdominios
/// ("doubleclick.net" bloquea "ad.doubleclick.net", no "notdoubleclick.net").
/// </summary>
public sealed class BlockList
{
    private readonly HashSet<string> _domains;

    public BlockList(IEnumerable<string> domains)
    {
        _domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in domains)
        {
            var d = Normalize(raw);
            if (d is not null) _domains.Add(d);
        }
    }

    public IReadOnlyCollection<string> Domains => _domains;

    /// <summary>Formato: un dominio por línea; '#' inicia comentario; acepta líneas estilo hosts ("0.0.0.0 dominio").</summary>
    public static BlockList Parse(string text) => new(text.Split('\n'));

    public bool IsBlocked(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var h = host.TrimEnd('.');
        while (true)
        {
            if (_domains.Contains(h)) return true;
            var dot = h.IndexOf('.');
            if (dot < 0) return false;
            h = h[(dot + 1)..];
        }
    }

    /// <summary>Filtros de URL para WebView2: dominio exacto y subdominios.</summary>
    public IEnumerable<string> UrlFilters()
    {
        foreach (var d in _domains)
        {
            yield return $"*://{d}/*";
            yield return $"*://*.{d}/*";
        }
    }

    private static string? Normalize(string line)
    {
        var hash = line.IndexOf('#');
        if (hash >= 0) line = line[..hash];
        var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var d = parts[^1].Trim().TrimEnd('.').ToLowerInvariant();
        if (d.Length == 0 || !d.Contains('.') || d == "localhost") return null;
        foreach (var c in d)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')) return null;
        return d;
    }
}
