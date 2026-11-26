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

    /// <summary>Carpeta de la voz neuronal dentro de la de modelos.</summary>
    public const string NaturalVoiceDir = "voz-ryan";
    private const string PiperRepo = "https://huggingface.co/csukuangfj/vits-piper-en_US-ryan-medium/resolve/2784cf0f7510312ed567a5d227bcbec28dcd661e/";

    /// <summary>
    /// Voz inglesa neuronal (<see cref="NaturalVoice"/>): Piper «ryan» calidad media (voz de hombre, inglés de Estados
    /// Unidos; 60 MB) más los datos de pronunciación del inglés de espeak-ng (0,8 MB de los 18 que trae completo).
    /// Licencias: la voz se entrenó con RyanSpeech (CC BY-NC-SA 4.0: uso no comercial); espeak-ng es GPL-3.0.
    /// </summary>
    public static readonly IReadOnlyList<Model> NaturalVoiceFiles =
    [
        Piper("en_US-ryan-medium.onnx", 63201425, "8a4063318faeda9bf67f89d25b29cc4bd5fde8832422aa451a5d5d2647553081"),
        Piper("tokens.txt", 921, "87c8ef66eae5473ed0cc0366b3964c736ca6c5f676c979522ea31234e47430b9"),
        Piper("espeak-ng-data/phondata", 550424, "4e0288957874029a8c3c9f41a8f517ad4bf18127046decbdd4b9d1d6807ce3a3"),
        Piper("espeak-ng-data/phonindex", 39074, "3ca7b8fa3b42624e4b0f152707e7a39245fce569aa99ea47c055d9e622fcf0c4"),
        Piper("espeak-ng-data/phontab", 55796, "886f3fa402cb0ba73d483aa8ad000af47a6b7cc06293c75a97913fba68a530f6"),
        Piper("espeak-ng-data/intonations", 2040, "3f8af65fd3eda9759a10f021d61361c120871f463515229c925995c7f90918cc"),
        Piper("espeak-ng-data/en_dict", 166944, "71bd330ba8a2e3e8076e631508208ef49449d6147c17b7bd2b4b1e1468292e35"),
        Piper("espeak-ng-data/lang/gmw/en-US", 257, "41534c2a22df5dd4f1052ff9e1a33a3ea7bff5a26b5c02bdad5ba8ddb7524704"),
        Piper("espeak-ng-data/lang/gmw/en", 140, "4605d5330801de3641c6e366d15f129ea1f5ffbce8722642aba01ace07ab9c83"),
    ];

    private static Model Piper(string file, long size, string sha256) =>
        new(NaturalVoiceDir + "/" + file, PiperRepo + file, size, sha256, "voz inglesa");

    /// <summary>Lo imprescindible para traducir: detector de voz y reconocedor.</summary>
    public static readonly IReadOnlyList<Model> Required = [Vad, Whisper];

    private const int Attempts = 3;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    public static string PathOf(string dir, Model model) => Path.Combine(dir, model.File);

    /// <summary>Deja los modelos pedidos en <paramref name="dir"/>. <paramref name="onProgress"/>: qué baja y cuánto (0..1).</summary>
    public static void Ensure(string dir, IReadOnlyList<Model> models, Action<string, double> onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        foreach (var old in Obsolete) File.Delete(Path.Combine(dir, old));
        var missing = models.Where(m => !IsPresent(dir, m)).ToList();
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
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
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
