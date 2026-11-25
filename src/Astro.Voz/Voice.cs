using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace Astro.Voz;

/// <summary>
/// Voz inglesa de Windows (SAPI): ~10 ms por frase en una PC común. Suena robótica pero es instantánea y no ocupa
/// memoria. Sólo si Windows tiene una voz en inglés instalada.
/// </summary>
internal sealed class Voice : IDisposable
{
    public const int Rate = 24000;
    private readonly SpeechSynthesizer _synth = new();
    private readonly SpeechAudioFormatInfo _format = new(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

    public string Name { get; }

    private Voice(InstalledVoice voice)
    {
        _synth.SelectVoice(voice.VoiceInfo.Name);
        _synth.Rate = 1; // un poco más rápido que lo normal (-10..10): la voz inglesa no se queda atrás de quien habla
        Name = voice.VoiceInfo.Name;
    }

    /// <summary>Una voz en inglés de Windows (prefiere Estados Unidos), o null si no hay.</summary>
    public static Voice? TryCreate()
    {
        try
        {
            using var probe = new SpeechSynthesizer();
            var voices = probe.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en").ToList();
            var voice = voices.FirstOrDefault(v => v.VoiceInfo.Culture.Name == "en-US") ?? voices.FirstOrDefault();
            if (voice is null) return null;
            var created = new Voice(voice);
            created.Speak("Ready."); // la primera frase tarda ~300 ms (carga la voz); las siguientes ~10 ms
            return created;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>PCM mono de 16 bits a <see cref="Rate"/> Hz. Un hilo a la vez.</summary>
    public byte[] Speak(string text)
    {
        using var ms = new MemoryStream();
        _synth.SetOutputToAudioStream(ms, _format);
        _synth.Speak(text);
        _synth.SetOutputToNull();
        return ms.ToArray();
    }

    public void Dispose() => _synth.Dispose();
}
