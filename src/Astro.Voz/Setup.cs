using Navegador.Core.Voz;

namespace Astro.Voz;

/// <summary>Todo lo que el traductor carga una vez: modelos, detector de voz, reconocedor, voz inglesa y glosario.</summary>
internal sealed class Setup : IDisposable
{
    public const string GlossaryFile = "voz-glosario.txt", LearnedFile = "voz-aprendido.json";

    public required SpeechDetector Detector { get; init; }
    public required Engine Engine { get; init; }
    public required IVoice Voice { get; init; }
    public Glossary? Glossary { get; init; }
    /// <summary>Dónde se guarda lo aprendido; null si no se aprende.</summary>
    public string? LearnedPath { get; init; }

    /// <param name="onStatus">Qué está preparando: fase ("downloading" con progreso 0..1, o "loading") y detalle.</param>
    public static Setup Load(Options options, Action<string, string, double> onStatus, CancellationToken ct)
    {
        ModelStore.Ensure(options.Models, ModelStore.Required, (label, progress) => onStatus("downloading", label, progress), ct);
        onStatus("loading", "reconocimiento de voz", 0);
        var detector = new SpeechDetector(ModelStore.PathOf(options.Models, ModelStore.Vad));
        var engine = Engine.Load(ModelStore.PathOf(options.Models, ModelStore.Whisper), options.Threads);
        onStatus("loading", "voz inglesa", 0);
        var voice = LoadVoice(options, onStatus, ct) ?? throw new InvalidOperationException(
            "No hay voz inglesa: no se pudo bajar la de Astro y Windows no tiene una instalada (Configuración → Hora e idioma → Voz → Agregar voces)");

        Glossary? glossary = null;
        string? learnedPath = null;
        if (options.Data is not null)
        {
            glossary = new Glossary();
            glossary.LoadUserFile(Path.Combine(options.Data, GlossaryFile));
            if (options.Learn)
            {
                learnedPath = Path.Combine(options.Data, LearnedFile);
                glossary.LoadLearned(learnedPath, DateOnly.FromDateTime(DateTime.Now).DayNumber);
            }
        }
        return new Setup { Detector = detector, Engine = engine, Voice = voice, Glossary = glossary, LearnedPath = learnedPath };
    }

    /// <summary>La voz neuronal si se pidió y se puede (sus archivos se bajan la primera vez); si no, la de Windows.</summary>
    private static IVoice? LoadVoice(Options options, Action<string, string, double> onStatus, CancellationToken ct)
    {
        if (options.NaturalVoice)
        {
            try
            {
                ModelStore.Ensure(options.Models, ModelStore.NaturalVoiceFiles, (label, progress) => onStatus("downloading", label, progress), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException
                                       && !ct.IsCancellationRequested)
            {
                // Sin conexión o descarga rota: esta reunión va con la voz de Windows; la próxima vuelve a intentar.
            }
            if (NaturalVoice.TryCreate(options.Models, options.Threads) is { } natural) return natural;
        }
        return WindowsVoice.TryCreate();
    }

    public void Dispose()
    {
        Engine.Dispose();
        Detector.Dispose();
        Voice.Dispose();
    }
}
