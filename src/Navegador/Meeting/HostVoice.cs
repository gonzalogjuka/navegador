using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace Navegador.Meeting;

/// <summary>
/// Voz inglesa de Windows (SAPI) para el traductor: ~10 ms por frase en una PC común, contra ~2 s de Kokoro en una GPU
/// integrada (medido 2026-11-25 en Iris Xe). Suena más robótica pero es instantánea. Sólo si Windows tiene una voz en
/// inglés instalada; si no, el motor usa Kokoro.
/// </summary>
public sealed class HostVoice : IDisposable
{
    public const int Rate = 24000;
    private readonly SpeechSynthesizer _synth = new();
    private readonly SpeechAudioFormatInfo _format = new(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
    private readonly object _lock = new();

    public string VoiceName { get; }

    private HostVoice(InstalledVoice voice)
    {
        _synth.SelectVoice(voice.VoiceInfo.Name);
        _synth.Rate = 1; // un poco más rápido que lo normal (-10..10): la voz inglesa no se queda atrás de quien habla
        VoiceName = voice.VoiceInfo.Name;
    }

    /// <summary>Una voz en inglés de Windows (prefiere Estados Unidos), o null si no hay.</summary>
    public static HostVoice? TryCreate()
    {
        try
        {
            using var probe = new SpeechSynthesizer();
            var voices = probe.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en").ToList();
            var voice = voices.FirstOrDefault(v => v.VoiceInfo.Culture.Name == "en-US") ?? voices.FirstOrDefault();
            if (voice is null) return null;
            var host = new HostVoice(voice);
            host.Speak("Ready."); // la primera frase tarda ~300 ms (carga la voz); las siguientes ~10 ms
            return host;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>PCM mono de 16 bits a <see cref="Rate"/> Hz.</summary>
    public byte[] Speak(string text)
    {
        lock (_lock)
        {
            using var ms = new MemoryStream();
            _synth.SetOutputToAudioStream(ms, _format);
            _synth.Speak(text);
            _synth.SetOutputToNull();
            return ms.ToArray();
        }
    }

    public void Dispose() => _synth.Dispose();
}
