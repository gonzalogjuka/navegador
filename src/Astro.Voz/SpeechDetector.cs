using Whisper.net;

namespace Astro.Voz;

/// <summary>
/// Detector de voz Silero (nativo, en el procesador: ~0,1 ms por ventana de 32 ms). El modelo se carga una vez; cada
/// pestaña en reunión tiene su propio estado, porque el detector recuerda lo que venía escuchando.
/// </summary>
internal sealed class SpeechDetector(string modelPath) : IDisposable
{
    private readonly WhisperVadFactory _factory = WhisperVadFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });

    /// <summary>Un detector con estado propio: recibe una ventana de 512 muestras a 16 kHz y dice si hay voz.</summary>
    public Stream NewStream()
    {
        var processor = _factory.CreateBuilder()
            .WithThreads(1).WithUseGpu(false).WithThreshold(0.5f)
            // Se le pregunta ventana por ventana: sin mínimos ni relleno, la respuesta es "hay voz en esta ventana o no".
            .WithMinSpeechDuration(TimeSpan.Zero).WithMinSilenceDuration(TimeSpan.Zero).WithSpeechPadding(TimeSpan.Zero)
            .Build();
        // Imprescindible: recién creado, el detector tiene en su memoria lo que hubiera ahí. Sin este reinicio, una de
        // cada dos reuniones arrancaba con el detector "sordo" (medido: 0 ventanas con voz en toda la corrida).
        processor.ResetState();
        return new Stream(processor);
    }

    public void Dispose() => _factory.Dispose();

    internal sealed class Stream(WhisperVadProcessor processor) : IDisposable
    {
        public bool IsSpeech(float[] window) => processor.DetectSpeechNoReset(window).Any();

        public void Dispose() => processor.Dispose();
    }
}
