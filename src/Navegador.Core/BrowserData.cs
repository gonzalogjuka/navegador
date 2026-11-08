using System.Text.Json;

namespace Navegador.Core;

public sealed record Bookmark(string Url, string Title);

public sealed class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime LastVisitUtc { get; set; }
    public int Visits { get; set; }
}

/// <summary>
/// Favoritos e historial locales (data.json). El historial guarda una entrada por URL (normalizada) y se recorta a
/// <see cref="MaxHistory"/> entradas (las visitadas hace más tiempo se van primero). Sólo http/https.
/// </summary>
public sealed class BrowserData
{
    public const int MaxHistory = 2000;

    public List<Bookmark> Bookmarks { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static bool IsRecordable(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>"https://a.com" y "https://a.com/" son la misma página.</summary>
    public static string Normalize(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsoluteUri : url;

    public bool IsBookmarked(string url)
    {
        var n = Normalize(url);
        return Bookmarks.Any(b => b.Url == n);
    }

    /// <summary>Agrega o quita el favorito. Devuelve true si quedó agregado.</summary>
    public bool ToggleBookmark(string url, string title)
    {
        url = Normalize(url);
        if (Bookmarks.RemoveAll(b => b.Url == url) > 0) return false;
        if (!IsRecordable(url)) return false;
        Bookmarks.Add(new Bookmark(url, string.IsNullOrWhiteSpace(title) ? url : title));
        return true;
    }

    public void AddVisit(string url, string title, DateTime nowUtc)
    {
        if (!IsRecordable(url)) return;
        url = Normalize(url);
        var e = History.FirstOrDefault(h => h.Url == url);
        if (e is null)
        {
            e = new HistoryEntry { Url = url };
            History.Add(e);
        }
        e.Visits++;
        e.LastVisitUtc = nowUtc;
        if (!string.IsNullOrWhiteSpace(title)) e.Title = title;
        if (History.Count > MaxHistory)
        {
            History.Sort((a, b) => b.LastVisitUtc.CompareTo(a.LastVisitUtc));
            History.RemoveRange(MaxHistory, History.Count - MaxHistory);
        }
    }

    public void UpdateTitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        url = Normalize(url);
        var e = History.FirstOrDefault(h => h.Url == url);
        if (e is not null) e.Title = title;
    }

    public IEnumerable<HistoryEntry> Recent(int count) => History.OrderByDescending(h => h.LastVisitUtc).Take(count);

    public void ClearHistory() => History.Clear();

    public static BrowserData Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var d = JsonSerializer.Deserialize<BrowserData>(File.ReadAllText(path)) ?? new();
            d.Bookmarks ??= [];
            d.History ??= [];
            return d;
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
