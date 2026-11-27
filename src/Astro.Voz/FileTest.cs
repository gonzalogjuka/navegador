using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Navegador.Core.Voz;

namespace Astro.Voz;

/// <summary>
/// Prueba sin navegador (--file): pasa un WAV a ritmo de reunión (un pedazo de ~21 ms cada 21 ms) por el mismo detector,
/// segmentador, traductor con adelanto, intérprete y voz que usa la reunión, y deja en JSON las frases (lo que entendió
/// en español y lo que dijo en inglés), la demora de cada una (de la última palabra a la voz inglesa) y la memoria del
/// proceso. Con --remote, otro WAV en inglés suena a la vez y hace de "los demás". Tarda lo que dura el audio.
/// </summary>
internal static class FileTest
{
    private sealed record PhraseResult(int AudioMs, string Reason, string? Es, string En, int QueueMs, int AsrMs, int LlmMs, int TtsMs, int TotalMs,
        bool Speculated, bool Interpreted, int Hints, bool Context, int Merged, int TtsAudioMs);

    private sealed record Result(int Threads, string? Voice, string Interpreter, int LoadMs, int InterpreterLoadMs, int DurationMs,
        IReadOnlyList<PhraseResult> Phrases, int TotalMedianMs, int TotalMaxMs, int AsrMedianMs, int AsrMaxMs, int LlmMedianMs, int Withdrawn,
        int Heard, int NotesExpressions, string? NotesAnswer, IReadOnlyList<string> Glossary, int PrivateMb, int WorkingSetMb, int PeakWorkingSetMb);

    public static int Run(Options options)
    {
        var started = Stopwatch.GetTimestamp();
        using var setup = Setup.Load(options, (_, _, _) => { }, CancellationToken.None);
        var loadMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // En la reunión el intérprete carga mientras ya se traduce; acá se lo espera, para medir con él desde la primera frase.
        var interpreterProblem = setup.LoadInterpreter(options, (_, _, _) => { }, CancellationToken.None);
        var interpreterLoadMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds - loadMs;

        var results = new List<PhraseResult>();
        var audioBytes = 0;
        var notesExpressions = -1;
        string? notesAnswer = null;
        var translator = new Translator(setup.Engine, setup.Voice, setup.Glossary, setup.LearnedPath)
        {
            OnAudio = (_, _, pcm) => audioBytes += pcm.Length,
            OnDone = r =>
            {
                results.Add(new PhraseResult(r.Info.AudioMs, r.Info.Reason, r.Spanish, r.English, r.Info.QueueMs, r.Info.AsrMs, r.Info.LlmMs,
                    r.Info.TtsMs, r.Info.TotalMs, r.Info.Speculated, r.Info.Interpreted, r.Info.Hints, r.Info.Context, r.Info.Merged,
                    audioBytes / 2 * 1000 / setup.Voice.Rate));
                audioBytes = 0;
            },
            OnError = ex => results.Add(new PhraseResult(0, "error", null, ex.Message, 0, 0, 0, 0, 0, false, false, 0, false, 1, 0)),
            OnNotes = count => notesExpressions = count,
            OnNotesAnswer = answer => notesAnswer = answer,
            Phrasebook = setup.Phrasebook, Notes = setup.Notes, NotesDir = setup.NotesDir,
        };
        translator.Interpreter = setup.Interpreter;
        var worker = new Thread(translator.Run) { IsBackground = true, Name = "traducir" };
        worker.Start();

        var pcm = ReadWavAs16k(options.File!);
        var remote = options.Remote is null ? [] : ReadWavAs16k(options.Remote);
        var withdrawn = 0;
        using (var stream = setup.Detector.NewStream())
        using (var remoteStream = setup.Detector.NewStream())
        {
            var segmenter = new PhraseSegmenter(stream.IsSpeech);
            var remoteSegmenter = new PhraseSegmenter(remoteStream.IsSpeech, Translator.HeardSegments);
            const int chunk = 682; // ~21 ms, como los pedazos que manda la pestaña en reunión
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < Math.Max(pcm.Length, remote.Length); i += chunk)
            {
                var due = i / 32.0 - clock.Elapsed.TotalMilliseconds;
                if (due > 1) Thread.Sleep((int)due);
                if (i < pcm.Length)
                {
                    segmenter.Push(pcm.AsSpan(i, Math.Min(chunk, pcm.Length - i)), Translator.NowMs(), p => translator.Confirm(0, p), t =>
                    {
                        if (t is not null) translator.Tentative(0, t);
                        else { translator.Withdraw(0); withdrawn++; }
                    });
                    translator.Speaking(segmenter.Active);
                }
                if (i < remote.Length)
                    remoteSegmenter.Push(remote.AsSpan(i, Math.Min(chunk, remote.Length - i)), Translator.NowMs(), p => translator.Heard(0, p));
            }
            segmenter.Flush(p => translator.Confirm(0, p));
            translator.Speaking(false);
            remoteSegmenter.Flush(p => translator.Heard(0, p));
        }
        // Lo que quedó en cola se termina (tus frases, lo de los demás y los apuntes) antes de cerrar.
        translator.SaveNotesSoon();
        for (var waited = 0; translator.Busy && waited < 90_000; waited += 20) Thread.Sleep(20);
        translator.Stop();
        worker.Join(TimeSpan.FromSeconds(5));

        var spoken = results.Where(r => r.En.Length > 0 && r.Reason != "error").ToList();
        var total = spoken.Select(r => r.TotalMs).Order().ToList();
        var asr = results.Select(r => r.AsrMs).Order().ToList();
        var llm = spoken.Where(r => r.Interpreted).Select(r => r.LlmMs).Order().ToList();
        using var me = Process.GetCurrentProcess();
        const int mb = 1024 * 1024;
        var result = new Result(options.Threads, setup.Voice.Name, interpreterProblem ?? ModelStore.InterpreterModel.File, loadMs, interpreterLoadMs,
            Math.Max(pcm.Length, remote.Length) / 2 * 1000 / Engine.Rate, results,
            total.Count > 0 ? total[total.Count / 2] : 0, total.Count > 0 ? total[^1] : 0,
            asr.Count > 0 ? asr[asr.Count / 2] : 0, asr.Count > 0 ? asr[^1] : 0, llm.Count > 0 ? llm[llm.Count / 2] : 0, withdrawn,
            translator.HeardDone, notesExpressions, notesAnswer,
            setup.Glossary is { } g ? [.. g.UserTerms, .. g.LearnedTerms] : [],
            (int)(me.PrivateMemorySize64 / mb), (int)(me.WorkingSet64 / mb), (int)(me.PeakWorkingSet64 / mb));
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
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
