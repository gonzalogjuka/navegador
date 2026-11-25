using System.Diagnostics;
using System.IO.Pipes;
using Navegador.Core.Voz;

namespace Astro.Voz;

/// <summary>
/// El traductor mientras dura la reunión. Dos hilos propios:
///   · el que lee el pipe: recibe tu voz, la pasa por el detector y corta las frases (nunca espera al reconocedor);
///   · el que traduce: una frase a la vez → Whisper → voz inglesa → de vuelta por el pipe.
/// Si las frases se atrasan, las pendientes de la misma pestaña se juntan en una para no acumular demora.
/// Termina cuando Astro manda Bye, cierra el pipe o deja de existir.
/// </summary>
internal sealed class Service : IDisposable
{
    /// <summary>Tope de una frase armada juntando atrasadas: más que esto se traduce por separado.</summary>
    private const int MaxMergedSamples = Engine.Rate * 20;

    private sealed record Job(int Tab, Phrase Phrase, int Merged);

    private readonly Options _options;
    private readonly NamedPipeClientStream _pipe;
    private readonly object _writeLock = new();
    private readonly object _queueLock = new();
    private readonly LinkedList<Job> _queue = new();
    private readonly Dictionary<int, (PhraseSegmenter Segmenter, SpeechDetector.Stream Detector)> _tabs = [];
    private readonly CancellationTokenSource _stop = new();
    private SpeechDetector? _detector;
    private Engine? _engine;
    private Voice? _voice;
    private volatile bool _ready;
    private bool _highPriority;
    /// <summary>Cada ~5 s de voz recibida (pedazos de ~21 ms) se le mandan los contadores a Astro.</summary>
    private const int StatsEveryFrames = 240;
    private long _micFrames, _windows, _voiced;
    private volatile int _phrases, _done;

    private Service(Options options, NamedPipeClientStream pipe)
    {
        _options = options;
        _pipe = pipe;
    }

    public static int Run(Options options)
    {
        using var pipe = new NamedPipeClientStream(".", options.Pipe!, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        pipe.Connect(15_000);
        using var service = new Service(options, pipe);
        service._highPriority = AudioPriority.ForThisProcess();
        service.WatchParent();
        var worker = new Thread(service.Work) { IsBackground = true, Name = "traducir" };
        worker.Start();
        service.ReadLoop();
        service._stop.Cancel();
        lock (service._queueLock) Monitor.PulseAll(service._queueLock);
        worker.Join(TimeSpan.FromSeconds(5));
        return 0;
    }

    /// <summary>Si Astro deja de existir (lo cerraron por la fuerza), el traductor no queda huérfano.</summary>
    private void WatchParent()
    {
        if (_options.Parent <= 0) return;
        try
        {
            var parent = Process.GetProcessById(_options.Parent);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => Environment.Exit(0);
            if (parent.HasExited) Environment.Exit(0);
        }
        catch (ArgumentException)
        {
            Environment.Exit(0); // ya no está
        }
    }

    // ---------- Hilo del pipe: voz que llega → frases ----------

    private void ReadLoop()
    {
        using var priority = AudioPriority.ForThisThread();
        try
        {
            while (VozProtocol.Read(_pipe) is (var type, var payload))
            {
                switch (type)
                {
                    case VozFrame.Mic when _ready:
                        var (tab, pcm) = VozProtocol.ParseMic(payload);
                        SegmenterOf(tab).Push(pcm.Span, NowMs(), phrase => Enqueue(tab, phrase));
                        if (++_micFrames % StatsEveryFrames == 0)
                            Send(VozProtocol.Message(VozFrame.Stats, new VozStats(_micFrames, _windows, _voiced, _phrases, _done)));
                        break;
                    case VozFrame.TabClosed:
                        CloseTab(VozProtocol.TabOf(payload));
                        break;
                    case VozFrame.Bye:
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException)
        {
            // Astro cerró el pipe o mandó algo que no es del protocolo: se termina.
        }
    }

    private PhraseSegmenter SegmenterOf(int tab)
    {
        if (_tabs.TryGetValue(tab, out var entry)) return entry.Segmenter;
        var detector = _detector!.NewStream();
        var segmenter = new PhraseSegmenter(window =>
        {
            var speech = detector.IsSpeech(window);
            _windows++;
            if (speech) _voiced++;
            return speech;
        });
        _tabs[tab] = (segmenter, detector);
        return segmenter;
    }

    private void CloseTab(int tab)
    {
        if (!_tabs.Remove(tab, out var entry)) return;
        entry.Segmenter.Flush(phrase => Enqueue(tab, phrase));
        entry.Detector.Dispose();
    }

    private void Enqueue(int tab, Phrase phrase)
    {
        lock (_queueLock)
        {
            _phrases++;
            _queue.AddLast(new Job(tab, phrase, 1));
            Monitor.Pulse(_queueLock);
        }
    }

    // ---------- Hilo que traduce ----------

    private void Work()
    {
        using var priority = AudioPriority.ForThisThread();
        try
        {
            Load(priority.Active);
        }
        catch (Exception ex)
        {
            Send(VozProtocol.Message(VozFrame.Error, new VozError("No se pudo preparar la traducción: " + ex.Message, Fatal: true)));
            Environment.Exit(3);
        }
        while (!_stop.IsCancellationRequested)
        {
            Job job;
            int pending;
            lock (_queueLock)
            {
                while (_queue.Count == 0)
                {
                    if (_stop.IsCancellationRequested) return;
                    Monitor.Wait(_queueLock);
                }
                job = TakeMerged();
                pending = _queue.Count;
            }
            try
            {
                Translate(job, pending);
            }
            catch (Exception ex) when (ex is not IOException and not ObjectDisposedException)
            {
                Send(VozProtocol.Message(VozFrame.Error, new VozError(ex.Message, Fatal: false)));
            }
        }
    }

    /// <summary>La primera frase pendiente, junto con las siguientes de la misma pestaña si ya había atraso.</summary>
    private Job TakeMerged()
    {
        var job = _queue.First!.Value;
        _queue.RemoveFirst();
        while (_queue.First is { Value: var next } && next.Tab == job.Tab
               && job.Phrase.Samples.Length + next.Phrase.Samples.Length <= MaxMergedSamples)
        {
            _queue.RemoveFirst();
            var joined = new float[job.Phrase.Samples.Length + next.Phrase.Samples.Length];
            job.Phrase.Samples.CopyTo(joined, 0);
            next.Phrase.Samples.CopyTo(joined, job.Phrase.Samples.Length);
            job = new Job(job.Tab, next.Phrase with { Samples = joined, SpeechMs = job.Phrase.SpeechMs + next.Phrase.SpeechMs }, job.Merged + 1);
        }
        return job;
    }

    private void Load(bool mmcss)
    {
        var started = Stopwatch.GetTimestamp();
        ModelStore.Ensure(_options.Models, (label, progress) =>
            Send(VozProtocol.Message(VozFrame.Status, new VozStatus("downloading", label, progress))), _stop.Token);
        Send(VozProtocol.Message(VozFrame.Status, new VozStatus("loading", "reconocimiento de voz")));
        _detector = new SpeechDetector(ModelStore.PathOf(_options.Models, ModelStore.Vad));
        _engine = Engine.Load(ModelStore.PathOf(_options.Models, ModelStore.Whisper), _options.Backend, _options.Threads);
        Send(VozProtocol.Message(VozFrame.Status, new VozStatus("loading", "voz inglesa")));
        _voice = Voice.TryCreate() ?? throw new InvalidOperationException(
            "Windows no tiene una voz en inglés instalada (Configuración → Hora e idioma → Voz → Agregar voces)");
        _ready = true;
        Send(VozProtocol.Message(VozFrame.Ready, new VozReady(_engine.Backend, ModelStore.Whisper.File, _voice.Name, _options.Threads,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, _highPriority, mmcss)));
    }

    private void Translate(Job job, int pending)
    {
        var phrase = job.Phrase;
        var t0 = Stopwatch.GetTimestamp();
        var english = _engine!.Translate(phrase.Samples);
        var t1 = Stopwatch.GetTimestamp();
        Dump(phrase, english);
        var spoken = english.Length > 0;
        if (spoken) Send(VozProtocol.Play(job.Tab, Voice.Rate, _voice!.Speak(english)));
        var t2 = Stopwatch.GetTimestamp();
        Send(VozProtocol.Message(VozFrame.Phrase, new VozPhrase(job.Tab, spoken, phrase.Reason,
            AudioMs: phrase.Samples.Length * 1000 / Engine.Rate,
            AsrMs: (int)Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds,
            TtsMs: (int)Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds,
            TotalMs: (int)(NowMs() - phrase.LastVoiceMs), // de tu última palabra a que la voz inglesa sale por el pipe
            job.Merged, pending, english.Length)));
        _done += job.Merged;
    }

    /// <summary>
    /// Sólo para afinar la traducción (tools/smoke/voz.ps1 -Volcar): si la variable de entorno ASTRO_VOZ_VOLCAR nombra una
    /// carpeta, cada frase queda ahí como la oyó el reconocedor (WAV) con su texto. Sin la variable no se guarda nada:
    /// lo que decís en una reunión no se escribe en ningún lado.
    /// </summary>
    private static readonly string? DumpDir = Environment.GetEnvironmentVariable("ASTRO_VOZ_VOLCAR") is { Length: > 0 } dir ? dir : null;
    private int _dumped;

    private void Dump(Phrase phrase, string english)
    {
        if (DumpDir is null) return;
        try
        {
            Directory.CreateDirectory(DumpDir);
            var name = Path.Combine(DumpDir, $"frase-{++_dumped:000}");
            using (var wav = new BinaryWriter(File.Create(name + ".wav")))
            {
                var bytes = phrase.Samples.Length * 2;
                wav.Write("RIFF"u8); wav.Write(36 + bytes); wav.Write("WAVEfmt "u8); wav.Write(16); wav.Write((short)1); wav.Write((short)1);
                wav.Write(Engine.Rate); wav.Write(Engine.Rate * 2); wav.Write((short)2); wav.Write((short)16); wav.Write("data"u8); wav.Write(bytes);
                foreach (var sample in phrase.Samples) wav.Write((short)Math.Clamp(sample * 32767, short.MinValue, short.MaxValue));
            }
            File.WriteAllText(name + ".txt", english);
        }
        catch (IOException) { }
    }

    private void Send(byte[] frame)
    {
        try
        {
            lock (_writeLock) _pipe.Write(frame);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _stop.Cancel(); // Astro ya no escucha
        }
    }

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        foreach (var (_, detector) in _tabs.Values) detector.Dispose();
        _engine?.Dispose();
        _detector?.Dispose();
        _voice?.Dispose();
        _stop.Dispose();
    }
}
