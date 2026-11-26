using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using SherpaOnnx;

namespace Astro.Voz;

/// <summary>La voz inglesa que escucha la reunión. Un hilo a la vez.</summary>
internal interface IVoice : IDisposable
{
    string Name { get; }
    /// <summary>Frecuencia del audio que devuelve <see cref="Speak"/>.</summary>
    int Rate { get; }
    /// <summary>PCM mono de 16 bits a <see cref="Rate"/> Hz.</summary>
    /// <param name="speed">1 = ritmo normal; más de 1, más rápido (para alcanzar a quien habla cuando hay atraso).</param>
    byte[] Speak(string text, float speed = 1f);
}

/// <summary>
/// Voz neuronal (Piper «ryan», con sherpa-onnx): suena a una persona, no a un lector de pantalla. Medido el 26/11
/// (i5-9600K, 3 hilos): ~100–200 ms por oración (1/20 de lo que dura al decirla) y ~190 MB de memoria.
/// </summary>
internal sealed class NaturalVoice : IVoice
{
    private readonly OfflineTts _tts;

    public string Name => "Piper en_US-ryan-medium";
    public int Rate => _tts.SampleRate;

    private NaturalVoice(OfflineTts tts) => _tts = tts;

    /// <summary>La voz, o null si faltan sus archivos o no se pudo cargar (se usa la de Windows).</summary>
    public static NaturalVoice? TryCreate(string modelsDir, int threads)
    {
        var dir = Path.Combine(modelsDir, ModelStore.NaturalVoiceDir);
        if (ModelStore.NaturalVoiceFiles.Any(m => !File.Exists(ModelStore.PathOf(modelsDir, m)))) return null;
        try
        {
            var config = new OfflineTtsConfig();
            config.Model.Vits.Model = Path.Combine(dir, "en_US-ryan-medium.onnx");
            config.Model.Vits.Tokens = Path.Combine(dir, "tokens.txt");
            config.Model.Vits.DataDir = Path.Combine(dir, "espeak-ng-data");
            config.Model.NumThreads = Math.Clamp(threads, 1, 4); // con más de 4 no mejora
            config.Model.Provider = "cpu";
            var voice = new NaturalVoice(new OfflineTts(config));
            voice.Speak("Ready."); // la primera pasada reserva memoria y tarda más
            return voice;
        }
        catch (Exception ex) when (ex is SEHException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
                                       or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public byte[] Speak(string text, float speed = 1f)
    {
        var samples = _tts.Generate(text, speed, 0).Samples;
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            pcm[2 * i] = (byte)value;
            pcm[2 * i + 1] = (byte)(value >> 8);
        }
        return pcm;
    }

    public void Dispose() => _tts.Dispose();
}

/// <summary>
/// Voz inglesa de Windows (SAPI): ~10 ms por frase y casi sin memoria, pero suena a lector de pantalla. Es el respaldo
/// de la voz neuronal (y lo que se usa con <c>--voice windows</c>). Sólo si Windows tiene una voz en inglés instalada.
/// </summary>
internal sealed class WindowsVoice : IVoice
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly SpeechAudioFormatInfo _format;

    public string Name { get; }
    public int Rate => 24000;

    private WindowsVoice(InstalledVoice voice)
    {
        _format = new(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
        _synth.SelectVoice(voice.VoiceInfo.Name);
        Name = voice.VoiceInfo.Name;
    }

    /// <summary>Una voz en inglés de Windows (prefiere Estados Unidos), o null si no hay.</summary>
    public static WindowsVoice? TryCreate()
    {
        try
        {
            using var probe = new SpeechSynthesizer();
            var voices = probe.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en").ToList();
            var voice = voices.FirstOrDefault(v => v.VoiceInfo.Culture.Name == "en-US") ?? voices.FirstOrDefault();
            if (voice is null) return null;
            var created = new WindowsVoice(voice);
            created.Speak("Ready."); // la primera frase tarda ~300 ms (carga la voz); las siguientes ~10 ms
            return created;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or ArgumentException or COMException)
        {
            return null;
        }
    }

    public byte[] Speak(string text, float speed = 1f)
    {
        using var ms = new MemoryStream();
        // Ritmo de SAPI: -10..10. 1 es un poco más rápido que lo normal; con atraso, 3.
        _synth.Rate = speed > 1.05f ? 3 : 1;
        _synth.SetOutputToAudioStream(ms, _format);
        _synth.Speak(text);
        _synth.SetOutputToNull();
        return ms.ToArray();
    }

    public void Dispose() => _synth.Dispose();
}
