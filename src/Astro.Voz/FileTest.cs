using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Navegador.Core.Voz;

namespace Astro.Voz;

/// <summary>
/// Prueba sin navegador (--file): traduce un WAV entero con el mismo detector, segmentador, reconocedor y voz que usa
/// la reunión, y deja en JSON las frases en inglés, lo que tardó cada una y la memoria del proceso.
/// </summary>
internal static class FileTest
{
    private sealed record PhraseResult(int AudioMs, string Reason, string En, int AsrMs, int TtsMs, int TtsAudioMs);

    private sealed record Result(string Backend, int Threads, string? Voice, int LoadMs, int DurationMs, IReadOnlyList<PhraseResult> Phrases,
        int AsrMedianMs, int AsrMaxMs, int PrivateMb, int WorkingSetMb, int PeakWorkingSetMb);

    public static int Run(Options options)
    {
        var started = Stopwatch.GetTimestamp();
        ModelStore.Ensure(options.Models, (_, _) => { }, CancellationToken.None);
        using var detector = new SpeechDetector(ModelStore.PathOf(options.Models, ModelStore.Vad));
        using var engine = Engine.Load(ModelStore.PathOf(options.Models, ModelStore.Whisper), options.Backend, options.Threads);
        using var voice = Voice.TryCreate();
        var loadMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var pcm = ReadWavAs16k(options.File!);
        var phrases = new List<Phrase>();
        using (var stream = detector.NewStream())
        {
            var segmenter = new PhraseSegmenter(stream.IsSpeech);
            const int chunk = 682; // ~21 ms, como los pedazos que manda la pestaña en reunión
            for (var i = 0; i < pcm.Length; i += chunk)
                segmenter.Push(pcm.AsSpan(i, Math.Min(chunk, pcm.Length - i)), i / 32.0, phrases.Add);
            segmenter.Flush(phrases.Add);
        }

        var results = new List<PhraseResult>();
        foreach (var phrase in phrases)
        {
            var t0 = Stopwatch.GetTimestamp();
            var english = engine.Translate(phrase.Samples);
            var t1 = Stopwatch.GetTimestamp();
            var audio = english.Length > 0 && voice is not null ? voice.Speak(english) : [];
            var t2 = Stopwatch.GetTimestamp();
            results.Add(new PhraseResult(phrase.Samples.Length * 1000 / Engine.Rate, phrase.Reason, english,
                (int)Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds, (int)Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds,
                audio.Length / 2 * 1000 / Voice.Rate));
        }

        var asr = results.Select(r => r.AsrMs).Order().ToList();
        using var me = Process.GetCurrentProcess();
        const int mb = 1024 * 1024;
        var result = new Result(engine.Backend, options.Threads, voice?.Name, loadMs, pcm.Length / 2 * 1000 / Engine.Rate, results,
            asr.Count > 0 ? asr[asr.Count / 2] : 0, asr.Count > 0 ? asr[^1] : 0,
            (int)(me.PrivateMemorySize64 / mb), (int)(me.WorkingSet64 / mb), (int)(me.PeakWorkingSet64 / mb));
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        if (options.Out is not null) File.WriteAllText(options.Out, json, new UTF8Encoding(false));
        else Console.Out.Write(json);
        return 0;
    }

    /// <summary>WAV PCM de 16 bits (mono o estéreo, cualquier frecuencia) → PCM mono de 16 bits a 16 kHz.</summary>
    private static byte[] ReadWavAs16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("no es un archivo WAV");
        int channels = 0, rate = 0, bits = 0, dataAt = -1, dataLength = 0;
        for (var at = 12; at + 8 <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToInt32(bytes, at + 4);
            if (id == "fmt ")
            {
                if (BitConverter.ToInt16(bytes, at + 8) != 1) throw new InvalidDataException("el WAV tiene que ser PCM sin comprimir");
                channels = BitConverter.ToInt16(bytes, at + 10);
                rate = BitConverter.ToInt32(bytes, at + 12);
                bits = BitConverter.ToInt16(bytes, at + 22);
            }
            else if (id == "data")
            {
                dataAt = at + 8;
                dataLength = Math.Min(size, bytes.Length - dataAt);
                break;
            }
            at += 8 + size + (size & 1);
        }
        if (dataAt < 0 || bits != 16 || channels is < 1 or > 2 || rate < 8000) throw new InvalidDataException("WAV no admitido (PCM de 16 bits, mono o estéreo)");

        var frames = dataLength / (2 * channels);
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++) sum += BitConverter.ToInt16(bytes, dataAt + 2 * (i * channels + c));
            mono[i] = sum / (32768f * channels);
        }
        var ratio = rate / (double)Engine.Rate;
        var output = new byte[(int)(frames / ratio) * 2];
        for (var i = 0; i < output.Length / 2; i++)
        {
            var x = i * ratio;
            var i0 = (int)x;
            var a = mono[i0];
            var b = i0 + 1 < frames ? mono[i0 + 1] : a;
            var sample = (short)Math.Clamp((a + (b - a) * (x - i0)) * 32767, short.MinValue, short.MaxValue);
            output[2 * i] = (byte)sample;
            output[2 * i + 1] = (byte)(sample >> 8);
        }
        return output;
    }
}
