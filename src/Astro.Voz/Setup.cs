using Navegador.Core.Voz;

namespace Astro.Voz;

/// <summary>Todo lo que el traductor carga una vez: modelos, detector de voz, reconocedor, voz inglesa y glosario.</summary>
internal sealed class Setup : IDisposable
{
    public const string GlossaryFile = "voz-glosario.txt", LearnedFile = "voz-aprendido.json";
    public const string PhrasebookFile = "voz-expresiones.txt", NotesFolder = "voz-reuniones";
    /// <summary>Memoria física libre que tiene que haber para cargar el intérprete (ocupa ~1,1 GB).</summary>
    private const long InterpreterNeedsFreeBytes = 2200L * 1024 * 1024;

    public required SpeechDetector Detector { get; init; }
    public required Engine Engine { get; init; }
    public required IVoice Voice { get; init; }
    public Glossary? Glossary { get; init; }
    /// <summary>Dónde se guarda lo aprendido; null si no se aprende.</summary>
    public string? LearnedPath { get; init; }
    /// <summary>Modismos y cómo decirlos; null sin carpeta de datos o sin intérprete.</summary>
    public Phrasebook? Phrasebook { get; init; }
    /// <summary>Apuntes de la reunión y su carpeta; null si no se guardan.</summary>
    public MeetingNotes? Notes { get; init; }
    public string? NotesDir { get; init; }
    /// <summary>Los términos propios como van en las instrucciones del intérprete ("" si no hay).</summary>
    public string GlossaryLine { get; init; } = "";
    public Interpreter? Interpreter { get; private set; }

    /// <param name="onStatus">Qué está preparando: fase ("downloading" con progreso 0..1, o "loading") y detalle.</param>
    public static Setup Load(Options options, Action<string, string, double> onStatus, CancellationToken ct)
    {
        ModelStore.Ensure(options.Models, ModelStore.Required, (label, progress) => onStatus("downloading", label, progress), ct);
        onStatus("loading", "reconocimiento de voz", 0);
        var detector = new SpeechDetector(ModelStore.PathOf(options.Models, ModelStore.Vad));
        // ASTRO_VOZ_MODELO: sólo para afinar, otro modelo ggml de Whisper (comparar base, small, medium con el mismo audio).
        var whisper = Environment.GetEnvironmentVariable("ASTRO_VOZ_MODELO") is { Length: > 0 } other
            ? other : ModelStore.PathOf(options.Models, ModelStore.Whisper);
        var engine = Engine.Load(whisper, options.Threads);
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
        var withData = options.Data is not null && options.Interpreter;
        return new Setup
        {
            Detector = detector, Engine = engine, Voice = voice, Glossary = glossary, LearnedPath = learnedPath,
            Phrasebook = withData ? Phrasebook.Load(Path.Combine(options.Data!, PhrasebookFile)) : null,
            Notes = withData && options.Notes ? new MeetingNotes() : null,
            NotesDir = withData && options.Notes ? Path.Combine(options.Data!, NotesFolder) : null,
            GlossaryLine = glossary is null ? "" : string.Join(", ", glossary.UserTerms.Concat(glossary.LearnedTerms).Take(30)),
        };
    }

    /// <summary>
    /// Carga el intérprete (baja el modelo la primera vez: 940 MB). Tarda: se llama con la reunión ya andando, desde
    /// otro hilo. Devuelve por qué no se cargó, o null si quedó listo en <see cref="Interpreter"/>.
    /// </summary>
    public string? LoadInterpreter(Options options, Action<string, string, double> onStatus, CancellationToken ct)
    {
        if (!options.Interpreter) return "apagado";
        // El modelo ya bajado en parte ocupa la caché de archivos de Windows, que cuenta como memoria disponible.
        var free = FreeMemoryBytes();
        if (free < InterpreterNeedsFreeBytes) return $"poca memoria libre ({free >> 20} MB)";
        try
        {
            ModelStore.Ensure(options.Models, [ModelStore.InterpreterModel], (label, progress) => onStatus("downloading", label, progress), ct);
            onStatus("loading", "intérprete", 0);
            Interpreter = Astro.Voz.Interpreter.Load(ModelStore.PathOf(options.Models, ModelStore.InterpreterModel), options.Threads, GlossaryLine);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException
                                       or InvalidOperationException or LLama.Exceptions.RuntimeError or LLama.Exceptions.LoadWeightsFailedException
                                       or TypeInitializationException or DllNotFoundException)
        {
            return ex.Message;
        }
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    /// <summary>Memoria física disponible ahora (si Windows no contesta, se supone que alcanza).</summary>
    private static long FreeMemoryBytes()
    {
        var status = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailPhys : long.MaxValue;
    }

    public void Dispose()
    {
        Interpreter?.Dispose();
        Engine.Dispose();
        Detector.Dispose();
        Voice.Dispose();
    }
}
