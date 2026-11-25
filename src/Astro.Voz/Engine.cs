using System.Text;
using Navegador.Core.Voz;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Astro.Voz;

/// <summary>
/// Whisper nativo (whisper.cpp vía Whisper.net): una frase en español entra, el texto en inglés sale (tarea
/// "translate"). El modelo se carga una vez; cada frase arma su propio estado con el contexto de audio justo para su
/// largo, que es lo que lo hace rápido: Whisper procesa siempre 30 s, y una frase de 5 s con contexto recortado tarda
/// ~250 ms en un procesador común contra ~1,5 s con el contexto entero (medido 2026-11-25, i5-1235U).
/// </summary>
internal sealed class Engine : IDisposable
{
    public const int Rate = 16000;
    /// <summary>whisper.cpp no procesa menos de 1 s de audio: las frases más cortas se completan con silencio.</summary>
    private const int MinSamples = Rate * 11 / 10;
    /// <summary>Una posición del contexto de audio = 20 ms. 1500 = los 30 s completos.</summary>
    private const int SamplesPerContext = 320, MinContext = 256, MaxContext = 1500, ContextMargin = 32;

    private readonly WhisperFactory _factory;
    private readonly int _threads;
    private readonly StringBuilder _text = new();

    /// <summary>"cpu" o "vulkan".</summary>
    public string Backend { get; }

    private Engine(WhisperFactory factory, string backend, int threads)
    {
        _factory = factory;
        Backend = backend;
        _threads = threads;
    }

    /// <param name="prefer">
    /// "auto": placa de video dedicada si hay (Vulkan), si no el procesador. En una integrada Vulkan gana ~100 ms por
    /// frase pero ocupa 450–700 MB de la memoria de la PC (la comparte con el sistema) contra ~180 MB del procesador.
    /// "cpu" y "vulkan" lo fuerzan.
    /// </param>
    public static Engine Load(string modelPath, string prefer, int threads)
    {
        var gpu = prefer switch
        {
            "cpu" => -1,
            "vulkan" => Math.Max(0, GpuProbe.DiscreteGpuIndex()),
            _ => GpuProbe.DiscreteGpuIndex(),
        };
        RuntimeOptions.RuntimeLibraryOrder = gpu >= 0
            ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
        var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = gpu >= 0, GpuDevice = Math.Max(0, gpu) });
        var vulkan = gpu >= 0 && RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan;
        var engine = new Engine(factory, vulkan ? "vulkan" : "cpu", threads);
        engine.Translate(new float[Rate * 2]); // precalentar: la primera pasada reserva memoria y tarda más
        return engine;
    }

    /// <summary>Texto en inglés de la frase, ya limpio; "" si no hay nada para decir.</summary>
    public string Translate(float[] samples)
    {
        if (samples.Length < MinSamples) Array.Resize(ref samples, MinSamples);
        var context = Math.Clamp(samples.Length / SamplesPerContext + ContextMargin, MinContext, MaxContext);
        // Tope de largo: Whisper a veces entra en bucle o inventa de más, y cada palabra de más es demora. Nadie dice más
        // de ~4 palabras por segundo: 7 tokens por segundo de audio más un margen alcanza de sobra.
        var maxTokens = Math.Min(200, samples.Length * 7 / Rate + 12);
        _text.Clear();
        // Sin reintentos con más temperatura (TemperatureInc 0): una sola pasada, demora pareja. Si igual repite,
        // PhraseText deja la repetición una sola vez.
        using var processor = _factory.CreateBuilder()
            .WithLanguage("es").WithTranslate().WithNoContext().WithSingleSegment()
            .WithThreads(_threads).WithTemperatureInc(0).WithAudioContextSize(context).WithMaxTokensPerSegment(maxTokens)
            .WithSegmentEventHandler(segment => _text.Append(segment.Text))
            .Build();
        processor.Process(samples);
        return PhraseText.Clean(_text.ToString());
    }

    public void Dispose() => _factory.Dispose();
}
