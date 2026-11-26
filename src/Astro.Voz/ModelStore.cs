using System.Security.Cryptography;

namespace Astro.Voz;

/// <summary>
/// Los modelos de voz (reconocimiento y detector de voz) se bajan la primera vez, con progreso, y quedan en la carpeta
/// de datos de Astro. Versión fija y contenido verificado (SHA-256): si lo bajado no es exactamente lo esperado, no se usa.
/// </summary>
internal static class ModelStore
{
    public sealed record Model(string File, string Url, long Size, string Sha256, string Label);

    /// <summary>
    /// Whisper small multilingüe cuantizado (q5_1): 181 MB. Reemplaza a base (57 MB): en la sesión en vivo del 26/11
    /// (55 frases de Gonzalo) small entendió el doble en frases de 1–2 s y no cambió el sentido ("Si ya te quiero decir
    /// algo" → base: "and you will want to die"; small: "If I want to tell you something"), a ~0,6 s por frase.
    /// </summary>
    public static readonly Model Whisper = new("ggml-small-q5_1.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small-q5_1.bin",
        190085487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", "reconocimiento de voz");

    /// <summary>Modelos que ya no se usan: se borran de la carpeta para no ocupar disco.</summary>
    private static readonly string[] Obsolete = ["ggml-base-q5_1.bin"];

    /// <summary>Silero VAD 5.1.2: menos de 1 MB.</summary>
    public static readonly Model Vad = new("ggml-silero-v5.1.2.bin",
        "https://huggingface.co/ggml-org/whisper-vad/resolve/9ffd54a1e1ee413ddf265af9913beaf518d1639b/ggml-silero-v5.1.2.bin",
        885098, "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf", "detector de voz");

    private const int Attempts = 3;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    public static string PathOf(string dir, Model model) => Path.Combine(dir, model.File);

    /// <summary>Deja los dos modelos en <paramref name="dir"/>. <paramref name="onProgress"/>: qué baja y cuánto (0..1).</summary>
    public static void Ensure(string dir, Action<string, double> onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        foreach (var old in Obsolete) File.Delete(Path.Combine(dir, old));
        var missing = new[] { Vad, Whisper }.Where(m => !IsPresent(dir, m)).ToList();
        if (missing.Count == 0) return;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("astro-voz");
        foreach (var model in missing)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Download(http, dir, model, p => onProgress(model.Label, p), ct);
                    break;
                }
                catch (Exception ex) when (attempt < Attempts && !ct.IsCancellationRequested
                                           && ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }
    }

    private static bool IsPresent(string dir, Model model)
    {
        var file = new FileInfo(PathOf(dir, model));
        return file.Exists && file.Length == model.Size;
    }

    private static void Download(HttpClient http, string dir, Model model, Action<double> onProgress, CancellationToken ct)
    {
        var target = PathOf(dir, model);
        var partial = target + ".part";
        using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            stall.CancelAfter(StallTimeout);
            using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, model.Url), HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            using var input = response.Content.ReadAsStream(stall.Token);
            using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long done = 0, lastReport = 0;
            onProgress(0);
            while (true)
            {
                stall.CancelAfter(StallTimeout); // el tope es por pedazo: una conexión lenta no falla, una colgada sí
                var read = input.ReadAsync(buffer, stall.Token).AsTask().GetAwaiter().GetResult();
                if (read == 0) break;
                output.Write(buffer, 0, read);
                sha.AppendData(buffer, 0, read);
                done += read;
                if (done > model.Size) throw new InvalidDataException($"{model.File}: llegó más de lo esperado");
                if (done - lastReport >= model.Size / 100)
                {
                    lastReport = done;
                    onProgress((double)done / model.Size);
                }
            }
            var hash = Convert.ToHexString(sha.GetHashAndReset());
            if (done != model.Size || !hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                output.Dispose();
                File.Delete(partial);
                throw new InvalidDataException($"{model.File}: lo bajado no coincide con lo esperado");
            }
        }
        File.Move(partial, target, overwrite: true);
        onProgress(1);
    }
}
