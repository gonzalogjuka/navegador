using System.Diagnostics;
using System.IO.Pipes;
using Navegador.Core.Voz;

namespace Astro.Voz;

/// <summary>
/// El traductor mientras dura la reunión. Dos hilos propios:
///   · el que lee el pipe: recibe tu voz, la pasa por el detector y corta las frases (nunca espera al reconocedor);
///   · el que traduce (<see cref="Translator"/>): Whisper → voz inglesa → de vuelta por el pipe.
/// Termina cuando Astro manda Bye, cierra el pipe o deja de existir.
/// </summary>
internal sealed class Service : IDisposable
{
    private readonly Options _options;
    private readonly NamedPipeClientStream _pipe;
    private readonly object _writeLock = new();
    private readonly Dictionary<int, (PhraseSegmenter Segmenter, SpeechDetector.Stream Detector)> _tabs = [];
    private readonly Dictionary<int, (PhraseSegmenter Segmenter, SpeechDetector.Stream Detector)> _remoteTabs = [];
    private readonly CancellationTokenSource _stop = new();
    private Setup? _setup;
    private volatile Translator? _translator;
    private bool _highPriority;
    /// <summary>Cada ~5 s de voz recibida (pedazos de ~21 ms) se le mandan los contadores a Astro.</summary>
    private const int StatsEveryFrames = 240;
    private long _micFrames, _windows, _voiced, _remoteFrames;

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
        service._translator?.Stop();
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
                    case VozFrame.Mic when _translator is { } translator:
                        var (tab, pcm) = VozProtocol.ParseMic(payload);
                        var segmenter = SegmenterOf(tab);
                        segmenter.Push(pcm.Span, Translator.NowMs(), phrase => translator.Confirm(tab, phrase), tentative =>
                        {
                            if (tentative is null) translator.Withdraw(tab);
                            else translator.Tentative(tab, tentative);
                        });
                        translator.Speaking(segmenter.Active || AnyoneSpeaking());
                        if (++_micFrames % StatsEveryFrames == 0)
                            Send(VozProtocol.Message(VozFrame.Stats, new VozStats(_micFrames, _windows, _voiced, translator.Phrases, translator.Done,
                                _remoteFrames, translator.HeardDone)));
                        break;
                    case VozFrame.Remote when _translator is { } translator:
                        var (remoteTab, remotePcm) = VozProtocol.ParseMic(payload);
                        _remoteFrames++;
                        RemoteSegmenterOf(remoteTab).Push(remotePcm.Span, Translator.NowMs(), phrase => translator.Heard(remoteTab, phrase));
                        break;
                    case VozFrame.TabClosed:
                        CloseTab(VozProtocol.TabOf(payload));
                        _translator?.SaveNotesSoon();
                        break;
                    case VozFrame.NoLearn:
                        _translator?.NoLearn(VozProtocol.TabOf(payload));
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
        var detector = _setup!.Detector.NewStream();
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

    /// <summary>Lo que dicen los demás se corta en frases igual que tu voz, con su propio detector. Sin adelantos.</summary>
    private PhraseSegmenter RemoteSegmenterOf(int tab)
    {
        if (_remoteTabs.TryGetValue(tab, out var entry)) return entry.Segmenter;
        var detector = _setup!.Detector.NewStream();
        var segmenter = new PhraseSegmenter(detector.IsSpeech, Translator.HeardSegments);
        _remoteTabs[tab] = (segmenter, detector);
        return segmenter;
    }

    private void CloseTab(int tab)
    {
        if (_tabs.Remove(tab, out var entry))
        {
            entry.Segmenter.Flush(phrase => _translator?.Confirm(tab, phrase));
            entry.Detector.Dispose();
            _translator?.Speaking(AnyoneSpeaking());
        }
        if (_remoteTabs.Remove(tab, out var remote))
        {
            remote.Segmenter.Flush(phrase => _translator?.Heard(tab, phrase));
            remote.Detector.Dispose();
        }
    }

    /// <summary>Alguna pestaña tiene una frase tuya en curso.</summary>
    private bool AnyoneSpeaking()
    {
        foreach (var (segmenter, _) in _tabs.Values)
            if (segmenter.Active) return true;
        return false;
    }

    // ---------- Hilo que traduce ----------

    private void Work()
    {
        using var priority = AudioPriority.ForThisThread();
        Translator translator;
        try
        {
            translator = Load(priority.Active);
        }
        catch (Exception ex)
        {
            Send(VozProtocol.Message(VozFrame.Error, new VozError("No se pudo preparar la traducción: " + ex.Message, Fatal: true)));
            Environment.Exit(3);
            return;
        }
        translator.Run();
    }

    private Translator Load(bool mmcss)
    {
        var started = Stopwatch.GetTimestamp();
        _setup = Setup.Load(_options, (phase, detail, progress) =>
            Send(VozProtocol.Message(VozFrame.Status, new VozStatus(phase, detail, progress))), _stop.Token);
        var translator = new Translator(_setup.Engine, _setup.Voice, _setup.Glossary, _setup.LearnedPath)
        {
            OnAudio = (tab, rate, pcm) => Send(VozProtocol.Play(tab, rate, pcm)),
            OnDone = result =>
            {
                Dump(result);
                Send(VozProtocol.Message(VozFrame.Phrase, result.Info));
            },
            OnError = ex => Send(VozProtocol.Message(VozFrame.Error, new VozError(ex.Message, Fatal: false))),
            OnNotes = expressions => Send(VozProtocol.Message(VozFrame.Notes, new VozNotes(expressions))),
            Phrasebook = _setup.Phrasebook, Notes = _setup.Notes, NotesDir = _setup.NotesDir,
        };
        Send(VozProtocol.Message(VozFrame.Ready, new VozReady(ModelStore.Whisper.File, _setup.Voice.Name, _options.Threads,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, _highPriority, mmcss,
            GlossaryTerms: _setup.Glossary is { } g ? g.UserTerms.Count + g.LearnedTerms.Count() : 0,
            Phrasebook: _setup.Phrasebook?.Count ?? 0)));
        _translator = translator; // desde acá el hilo del pipe le pasa la voz
        // El intérprete tarda en cargar (y la primera vez hay que bajarlo): la reunión ya anda con la traducción directa
        // y pasa a interpretar cuando está listo.
        new Thread(() => LoadInterpreter(translator)) { IsBackground = true, Name = "cargar intérprete" }.Start();
        return translator;
    }

    private void LoadInterpreter(Translator translator)
    {
        var started = Stopwatch.GetTimestamp();
        string? problem;
        try
        {
            problem = _setup!.LoadInterpreter(_options, (phase, detail, progress) =>
                Send(VozProtocol.Message(VozFrame.Status, new VozStatus(phase, detail, progress))), _stop.Token);
        }
        catch (Exception ex)
        {
            problem = ex.Message; // pase lo que pase con el intérprete, la traducción directa sigue
        }
        if (problem is null) translator.Interpreter = _setup!.Interpreter;
        Send(VozProtocol.Message(VozFrame.Interpreter, new VozInterpreter(problem is null, problem ?? ModelStore.InterpreterModel.File,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
    }

    /// <summary>
    /// Sólo para afinar la traducción (tools/smoke/voz.ps1 -Volcar): si la variable de entorno ASTRO_VOZ_VOLCAR nombra una
    /// carpeta, cada frase queda ahí como la oyó el reconocedor (WAV) con su texto. Sin la variable no se guarda nada:
    /// lo que decís en una reunión no se escribe en ningún lado.
    /// </summary>
    private static readonly string? DumpDir = Environment.GetEnvironmentVariable("ASTRO_VOZ_VOLCAR") is { Length: > 0 } dir ? dir : null;
    private int _dumped;

    private void Dump(Translator.Result result)
    {
        var (phrase, english) = (result.Phrase, result.Spanish is null ? result.English : result.Spanish + "\n" + result.English);
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
            _translator?.Stop();
        }
    }

    public void Dispose()
    {
        foreach (var (_, detector) in _tabs.Values) detector.Dispose();
        foreach (var (_, detector) in _remoteTabs.Values) detector.Dispose();
        _setup?.Dispose();
        _stop.Dispose();
    }
}
