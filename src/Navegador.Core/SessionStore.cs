using System.Text.Json;

namespace Navegador.Core;

public sealed record SavedTab(string Url, string Title);

public sealed record SavedSession(List<SavedTab> Tabs, int ActiveIndex);

/// <summary>Pestañas abiertas entre corridas. Escritura atómica (archivo temporal + reemplazo).</summary>
public static class SessionStore
{
    public static SavedSession? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(path));
            if (s is null || s.Tabs.Count == 0) return null;
            return s with { ActiveIndex = Math.Clamp(s.ActiveIndex, 0, s.Tabs.Count - 1) };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(string path, SavedSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(session));
        File.Move(tmp, path, overwrite: true);
    }
}
