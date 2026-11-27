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

    private Engine(WhisperFactory factory, int threads)
    {
        _factory = factory;
        _threads = threads;
    }

    /// <summary>
    /// Sólo el procesador, nunca la placa de video: Astro no lleva la biblioteca de GPU de Whisper. Con una dedicada
    /// (RTX 2060 SUPER) Vulkan tardaba 15 s en arrancar la primera vez y las primeras frases 9 s; en una integrada ocupa
    /// 450–700 MB de la memoria de la PC.
    /// </summary>
    public static Engine Load(string modelPath, int threads)
    {
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
        var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
        var engine = new Engine(factory, threads);
        engine.Translate(new float[Rate * 2]); // precalentar: la primera pasada reserva memoria y tarda más
        return engine;
    }

    /// <summary>Qué se le pide al reconocedor.</summary>
    public enum Mode
    {
        /// <summary>Español hablado → texto en inglés (la traducción directa de Whisper, sin intérprete).</summary>
        SpanishToEnglish,
        /// <summary>Español hablado → texto en español (lo que después interpreta el modelo de lenguaje).</summary>
        Spanish,
        /// <summary>Inglés hablado → texto en inglés (lo que dicen los demás en la reunión).</summary>
        English,
    }

    /// <summary>Texto en inglés de la frase, ya limpio; "" si no hay nada para decir.</summary>
    public string Translate(float[] samples, string? prompt = null, CancellationToken cancel = default) =>
        Recognize(samples, Mode.SpanishToEnglish, prompt, cancel);

    /// <summary>Texto de la frase, ya limpio; "" si no hay nada.</summary>
    /// <param name="prompt">
    /// Pista con el vocabulario propio (<see cref="Glossary.Prompt"/>): Whisper escribe bien los nombres que ve ahí.
    /// La frase anterior NO va de pista: medido, tarda más y traduce peor.
    /// </param>
    /// <param name="cancel">Corta el reconocimiento en curso (un adelanto que ya no sirve) en ~15 ms.</param>
    /// <param name="threads">Hilos para esta frase (0 = los del traductor). Lo que dicen los demás se reconoce con menos.</param>
    /// <exception cref="OperationCanceledException">Se cortó por <paramref name="cancel"/>.</exception>
    public string Recognize(float[] samples, Mode mode, string? prompt = null, CancellationToken cancel = default, int threads = 0)
    {
        if (samples.Length < MinSamples) Array.Resize(ref samples, MinSamples);
        var context = Math.Clamp(samples.Length / SamplesPerContext + ContextMargin, MinContext, MaxContext);
        // Tope de largo: Whisper a veces entra en bucle o inventa de más, y cada palabra de más es demora. Nadie dice más
        // de ~4 palabras por segundo: 7 tokens por segundo de audio más un margen alcanza de sobra.
        var maxTokens = Math.Min(200, samples.Length * 7 / Rate + 12);
        _text.Clear();
        // Sin reintentos con más temperatura (TemperatureInc 0): una sola pasada, demora pareja. Si igual repite,
        // PhraseText deja la repetición una sola vez.
        var builder = _factory.CreateBuilder()
            .WithLanguage(mode == Mode.English ? "en" : "es").WithNoContext().WithSingleSegment()
            .WithThreads(threads > 0 ? threads : _threads).WithTemperatureInc(0).WithAudioContextSize(context).WithMaxTokensPerSegment(maxTokens);
        if (mode == Mode.SpanishToEnglish) builder.WithTranslate();
        if (!string.IsNullOrEmpty(prompt)) builder.WithPrompt(prompt);
        var processor = builder.Build();
        try
        {
            foreach (var segment in processor.ProcessAsync(samples, cancel).ToBlockingEnumerable(cancel))
                _text.Append(segment.Text);
        }
        finally
        {
            // DisposeAsync espera a que whisper.cpp suelte la frase (cortada o no); Dispose a secas no se puede usar ahí.
            processor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        cancel.ThrowIfCancellationRequested();
        return PhraseText.Clean(_text.ToString());
    }

    public void Dispose() => _factory.Dispose();
}
