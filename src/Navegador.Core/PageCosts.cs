namespace Navegador.Core;

/// <summary>
/// Cuánta memoria de páginas (renderers + GPU) sumó cargar cada sitio: la subida máxima en los Window segundos
/// después de empezar a cargarlo. Sirve para hacer lugar antes de la próxima carga del mismo sitio: Google Maps sube
/// ~550 MB de golpe, cuatro veces una página promedio, y con el promedio el tope se pasaba igual. Si se cargan varias
/// páginas a la vez la subida se le atribuye entera a cada una: estima de más, nunca de menos. Sólo en memoria (no se
/// guarda en disco) y sólo para ventanas normales.
/// </summary>
public sealed class PageCosts
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, double> _cost = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double Start, double Peak, DateTime Until)> _loading = new(StringComparer.OrdinalIgnoreCase);

    public double? CostOf(string? host) => host is not null && _cost.TryGetValue(host, out var c) ? c : null;

    /// <summary>Empieza a cargar <paramref name="host"/> con <paramref name="pagesMb"/> de memoria de páginas medida.</summary>
    public void Start(string host, double pagesMb, DateTime nowUtc)
    {
        if (_loading.ContainsKey(host)) return; // ya se está midiendo una carga de este sitio
        _loading[host] = (pagesMb, pagesMb, nowUtc + Window);
    }

    /// <summary>Nueva muestra: actualiza los picos y cierra las cargas que ya cumplieron la ventana.</summary>
    public void Observe(double pagesMb, DateTime nowUtc)
    {
        foreach (var (host, l) in _loading.ToList())
        {
            var peak = Math.Max(l.Peak, pagesMb);
            if (nowUtc < l.Until)
            {
                _loading[host] = l with { Peak = peak };
                continue;
            }
            _loading.Remove(host);
            // La última medición manda (la página pudo cambiar), pero sin olvidar de golpe un sitio pesado.
            var rise = Math.Max(0, peak - l.Start);
            _cost[host] = _cost.TryGetValue(host, out var old) ? Math.Max(rise, old / 2) : rise;
        }
    }
}
