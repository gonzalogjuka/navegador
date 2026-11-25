namespace Navegador.Core;

/// <summary>
/// Sitios de reuniones donde Astro prende el modo reunión solo, antes de que cargue la página (fase 7.2): entrar a la
/// reunión y dar permiso al micrófono alcanza. En otros sitios queda el botón del micrófono.
/// </summary>
public static class MeetingSites
{
    private static readonly string[] Hosts =
    [
        "meet.google.com",
        "teams.microsoft.com", "teams.live.com", "teams.cloud.microsoft",
        "zoom.us", "zoom.com",   // y subdominios: app.zoom.us, us02web.zoom.us, empresa.zoom.us…
        "whereby.com",
        "meet.jit.si",
    ];

    public static bool IsMeeting(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        host = host.ToLowerInvariant().TrimEnd('.');
        return Hosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }
}
