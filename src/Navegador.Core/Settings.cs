using System.Text.Json;

namespace Navegador.Core;

/// <summary>Ajustes del usuario en settings.json. Valores ausentes o inválidos toman el valor por defecto.</summary>
public sealed class Settings
{
    public int RamBudgetMb { get; set; } = 1024;
    public int SuspendAfterSeconds { get; set; } = 60;
    public int DiscardAfterMinutes { get; set; } = 15;
    public int FocusLostSeconds { get; set; } = 10;
    /// <summary>
    /// El tope de RAM no descarta las BudgetKeepRecentTabs pestañas de fondo usadas más recientemente si las usaste en
    /// los últimos BudgetKeepRecentMinutes: evita descartar y recargar en bucle la pestaña a la que volvés.
    /// </summary>
    public int BudgetKeepRecentTabs { get; set; } = 2;
    public int BudgetKeepRecentMinutes { get; set; } = 3;
    public bool BlockingEnabled { get; set; } = true;
    public string SearchUrl { get; set; } = "https://www.google.com/search?q=";
    /// <summary>Sitios (host de la pestaña) donde no se bloquea nada.</summary>
    public List<string> AllowSites { get; set; } = [];
    /// <summary>Argumentos de Chromium para WebView2. Se aplican al reiniciar.</summary>
    public string BrowserArguments { get; set; } = "--process-per-site";
    /// <summary>Reintenta http:// como https:// y vuelve a http sólo si el sitio no tiene HTTPS.</summary>
    public bool HttpsFirst { get; set; } = true;
    /// <summary>Rechaza los pedidos de notificaciones de los sitios sin preguntar.</summary>
    public bool BlockNotifications { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool IsAllowed(string? host) =>
        !string.IsNullOrEmpty(host) && AllowSites.Contains(host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Alterna el permiso del sitio. Devuelve true si quedó permitido.</summary>
    public bool ToggleAllowed(string host)
    {
        if (AllowSites.RemoveAll(h => h.Equals(host, StringComparison.OrdinalIgnoreCase)) > 0) return false;
        AllowSites.Add(host.ToLowerInvariant());
        return true;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static Settings LoadOrCreate(string path)
    {
        Settings s;
        try
        {
            s = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            s = new();
        }
        s.Sanitize();
        if (!File.Exists(path)) s.Save(path);
        return s;
    }

    private void Sanitize()
    {
        var d = new Settings();
        if (RamBudgetMb < 128) RamBudgetMb = d.RamBudgetMb;
        if (SuspendAfterSeconds < 1) SuspendAfterSeconds = d.SuspendAfterSeconds;
        if (DiscardAfterMinutes < 1) DiscardAfterMinutes = d.DiscardAfterMinutes;
        if (FocusLostSeconds < 0) FocusLostSeconds = d.FocusLostSeconds;
        if (BudgetKeepRecentMinutes < 0) BudgetKeepRecentMinutes = d.BudgetKeepRecentMinutes;
        if (BudgetKeepRecentTabs < 0) BudgetKeepRecentTabs = d.BudgetKeepRecentTabs;
        if (string.IsNullOrWhiteSpace(SearchUrl)) SearchUrl = d.SearchUrl;
        BrowserArguments ??= "";
        AllowSites ??= [];
    }
}
