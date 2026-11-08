namespace Navegador.Core;

/// <summary>
/// Snapshots de pestañas: una imagen JPEG por pestaña en la carpeta snapshots, que se muestra mientras la pestaña
/// descartada despierta. Los nombres son aleatorios (no revelan la URL) y sólo se aceptan los generados acá,
/// para que un session.json editado no pueda apuntar a otro archivo.
/// </summary>
public static class SnapshotFiles
{
    public const string Extension = ".jpg";

    public static string NewName() => Guid.NewGuid().ToString("N") + Extension;

    public static bool IsValidName(string? name) =>
        name is { Length: 36 } && name.EndsWith(Extension, StringComparison.Ordinal)
        && name[..32].All(char.IsAsciiHexDigitLower);

    /// <summary>Archivos de la carpeta que ninguna pestaña referencia (quedaron de pestañas cerradas o corridas viejas).</summary>
    public static IEnumerable<string> Orphans(IEnumerable<string> existingFileNames, IEnumerable<string?> referenced)
    {
        var keep = new HashSet<string>(referenced.Where(r => r is not null)!, StringComparer.OrdinalIgnoreCase);
        return existingFileNames.Where(f => !keep.Contains(f));
    }
}
