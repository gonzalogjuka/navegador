using System.Diagnostics;
using Navegador.Core.Voz;

namespace Astro.Voz;

/// <summary>
/// El hilo que traduce. Con el intérprete: Whisper escribe en español lo que dijiste → el modelo de lenguaje lo dice en
/// inglés, por el sentido y con el contexto de la charla → voz inglesa. Sin el intérprete (apagado, todavía cargando, o
/// si falla en una frase): Whisper traduce directo al inglés.
/// Lo alimenta el hilo que corta las frases, con tres avisos por pestaña:
///   · <see cref="Tentative"/>: "probablemente terminó la frase" (128 ms de silencio). Se empieza a traducir ya.
///   · <see cref="Withdraw"/>: era una pausa para respirar, siguió hablando. Lo adelantado se corta y se tira.
///   · <see cref="Confirm"/>: la frase terminó. Si es la que se venía adelantando, la traducción ya está hecha o en
///     camino; si no, se traduce ahora.
/// Así la demora no incluye la pausa que confirma el fin de la frase (650 ms): se traduce durante esa pausa. Nada
/// suena antes de esa confirmación.
/// Cuando no hay nada tuyo para traducir, el mismo hilo escribe lo que dicen los demás (<see cref="Heard"/>): es el
/// contexto de tu próxima frase y de donde salen las expresiones para aprender. Tu voz siempre va primero: si empezás
/// a hablar, lo de los demás se corta.
/// Si las frases se atrasan, las pendientes de la misma pestaña se juntan en una y la voz va un poco más rápido.
/// </summary>
internal sealed class Translator(Engine engine, IVoice voice, Glossary? glossary, string? learnedPath)
{
    /// <summary>Tope de una frase armada juntando atrasadas: más que esto se traduce por separado.</summary>
    private const int MaxMergedSamples = Engine.Rate * 20;
    /// <summary>Ritmo de la voz cuando hay frases esperando.</summary>
    private const float CatchUpSpeed = 1.12f;
    /// <summary>Lo que dijeron los demás sirve de contexto de tu frase si es de hace menos que esto.</summary>
    private const double HeardFreshMs = 45_000;
    /// <summary>Largo máximo del contexto que se le pasa al intérprete (lo último que se dijo).</summary>
    private const int MaxHeardChars = 220;
    /// <summary>Frases de los demás en espera: si se juntan más, se pierden las más viejas (tu voz va primero).</summary>
    private const int MaxHeardQueue = 6;
    /// <summary>Hilos para escribir lo que dicen los demás: pocos, para no molestar al video de la reunión.</summary>
    private const int HeardThreads = 2;

    private sealed class Job(int tab, Phrase phrase, bool confirmed)
    {
        public int Tab { get; } = tab;
        public Phrase Phrase { get; set; } = phrase;
        /// <summary>Se empezó como adelanto, antes de saber si la frase había terminado.</summary>
        public bool Speculative { get; } = !confirmed;
        public bool Confirmed { get; set; } = confirmed;
        public bool Withdrawn { get; set; }
        public int Merged { get; set; } = 1;
        public CancellationTokenSource Cancel { get; } = new();
        public long Created { get; } = Stopwatch.GetTimestamp();
    }

    /// <summary>Una frase terminada: sus tiempos (lo que va al log) y su texto (que no sale de este proceso).</summary>
    /// <param name="Spanish">Lo que escribió Whisper en español; null si la frase se tradujo directo.</param>
    public sealed record Result(VozPhrase Info, Phrase Phrase, string English, string? Spanish);

    private readonly object _lock = new();
    private readonly LinkedList<Job> _queue = new();
    private readonly Dictionary<int, Job> _tentative = [];
    private readonly Queue<(int Tab, Phrase Phrase)> _heardQueue = new();
    private readonly Dictionary<int, (string Text, double AtMs)> _heard = [];
    private readonly HashSet<int> _noLearn = [];
    private CancellationTokenSource? _background; // lo que se está haciendo mientras no hablás (se corta si hablás)
    private bool _notesDue;
    private bool _stop, _working, _speaking;
    private double _speakingAtMs;
    private volatile int _phrases, _done, _heardDone;

    /// <summary>Voz traducida lista: pestaña, Hz, PCM. Sale de a tramos.</summary>
    public required Action<int, int, byte[]> OnAudio { get; init; }
    public required Action<Result> OnDone { get; init; }
    public required Action<Exception> OnError { get; init; }
    /// <summary>Se guardaron apuntes de la reunión: cuántas expresiones (sólo la cuenta va al log).</summary>
    public Action<int>? OnNotes { get; init; }
    /// <summary>Sólo para afinar (la prueba --file): lo que contestó el modelo cuando se le pidieron las expresiones.</summary>
    public Action<string>? OnNotesAnswer { get; init; }

    /// <summary>El intérprete, cuando termina de cargar (puede llegar con la reunión empezada). null: Whisper traduce directo.</summary>
    public volatile Interpreter? Interpreter;
    /// <summary>Modismos y cómo decirlos (pistas para el intérprete).</summary>
    public Phrasebook? Phrasebook { get; init; }
    /// <summary>Apuntes de la reunión y la carpeta donde se guardan; null si no se guardan.</summary>
    public MeetingNotes? Notes { get; init; }
    public string? NotesDir { get; init; }

    /// <summary>Frases confirmadas / terminadas (traducidas, con o sin algo para decir) / de los demás escritas.</summary>
    public int Phrases => _phrases;
    public int Done => _done;
    public int HeardDone => _heardDone;
    /// <summary>Queda algo por hacer (frases tuyas, de los demás o apuntes) o se está haciendo.</summary>
    public bool Busy
    {
        get
        {
            lock (_lock) return _working || _queue.Count > 0 || _heardQueue.Count > 0 || _notesDue;
        }
    }

    public static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    // ---------- Desde el hilo que corta las frases ----------

    public void Tentative(int tab, Phrase phrase)
    {
        lock (_lock)
        {
            Drop(tab);
            var job = new Job(tab, phrase, confirmed: false);
            _tentative[tab] = job;
            _queue.AddLast(job);
            _background?.Cancel();
            Monitor.PulseAll(_lock);
        }
    }

    public void Withdraw(int tab)
    {
        lock (_lock)
        {
            Drop(tab);
            Monitor.PulseAll(_lock);
        }
    }

    public void Confirm(int tab, Phrase phrase)
    {
        lock (_lock)
        {
            _phrases++;
            // El adelanto vale si es exactamente el mismo audio (el segmentador lo garantiza si no volviste a hablar).
            if (_tentative.TryGetValue(tab, out var job) && job.Phrase.Samples.Length == phrase.Samples.Length)
            {
                _tentative.Remove(tab);
                job.Phrase = phrase;
                job.Confirmed = true;
            }
            else
            {
                Drop(tab);
                _queue.AddLast(new Job(tab, phrase, confirmed: true));
                _background?.Cancel();
            }
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Una frase de los demás (lo que suena en la reunión). Se escribe cuando no hay nada tuyo para traducir.</summary>
    public void Heard(int tab, Phrase phrase)
    {
        lock (_lock)
        {
            _heardQueue.Enqueue((tab, phrase));
            while (_heardQueue.Count > MaxHeardQueue) _heardQueue.Dequeue();
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Cómo se cortan en frases lo que dicen los demás: pedazos cortos, que se escriben (y se cortan) rápido.</summary>
    public static readonly SegmenterOptions HeardSegments = new()
    {
        PauseMs = 500, MinPhraseMs = 300, ShortHoldMs = 500, LongPhraseMs = 4000, MaxPhraseMs = 6000,
    };

    /// <summary>
    /// Estás hablando (hay una frase tuya en curso) o dejaste de hablar; se avisa con cada pedazo de voz. Mientras hablás
    /// no se empieza nada de fondo y lo que estuviera en curso se corta ya, sin esperar al final de la frase: cortar una
    /// frase larga de los demás puede tardar hasta ~1 s (medido), y así ese segundo pasa mientras todavía hablás.
    /// Si dejan de llegar avisos (silenciaste el micrófono a mitad de una frase), a los 1,5 s se da por terminado.
    /// </summary>
    public void Speaking(bool speaking)
    {
        lock (_lock)
        {
            if (speaking) _speakingAtMs = NowMs();
            if (_speaking == speaking) return;
            _speaking = speaking;
            if (speaking) _background?.Cancel();
            Monitor.PulseAll(_lock);
        }
    }

    private const double SpeakingStaleMs = 1500;
    private bool SpeakingNow => _speaking && NowMs() - _speakingAtMs < SpeakingStaleMs;

    /// <summary>La reunión terminó o soltó el micrófono: cuando haya un rato libre se guardan los apuntes.</summary>
    public void SaveNotesSoon()
    {
        lock (_lock)
        {
            _notesDue = Notes is not null;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Lo que se diga o se oiga en esta pestaña no se aprende ni se anota (ventana privada).</summary>
    public void NoLearn(int tab)
    {
        lock (_lock) _noLearn.Add(tab);
    }

    public void Stop()
    {
        lock (_lock)
        {
            _stop = true;
            foreach (var job in _tentative.Values) job.Cancel.Cancel();
            _background?.Cancel();
            Monitor.PulseAll(_lock);
        }
    }

    private void Drop(int tab)
    {
        if (!_tentative.Remove(tab, out var job)) return;
        job.Withdrawn = true;
        job.Cancel.Cancel();
        _queue.Remove(job);
    }

    // ---------- El hilo que traduce ----------

    public void Run()
    {
        while (true)
        {
            Job? job = null;
            (int Tab, Phrase Phrase)? heard = null;
            var notes = false;
            CancellationToken background = default;
            lock (_lock)
            {
                // Con una frase tuya en curso sólo se espera (con tope, por si los avisos dejaron de llegar).
                while (_queue.Count == 0 && (SpeakingNow || (_heardQueue.Count == 0 && !_notesDue)) && !_stop)
                    Monitor.Wait(_lock, _speaking ? 500 : Timeout.Infinite);
                if (_stop) return;
                _working = true;
                if (_queue.Count > 0)
                {
                    job = _queue.First!.Value;
                    _queue.RemoveFirst();
                    if (job.Confirmed) Merge(job);
                }
                else
                {
                    // Nada tuyo: lo de los demás, y si tampoco, los apuntes. Cualquier frase tuya lo corta.
                    if (_heardQueue.Count > 0) heard = _heardQueue.Dequeue();
                    else notes = true;
                    _background = new CancellationTokenSource();
                    background = _background.Token;
                }
            }
            try
            {
                if (job is not null) Process(job);
                else if (heard is { } h) Listen(h.Tab, h.Phrase, background);
                else if (notes) SaveNotes(background);
            }
            catch (OperationCanceledException)
            {
                // Empezaste a hablar: lo de fondo se deja (los apuntes quedan para el próximo rato libre).
            }
            catch (Exception ex) when (ex is not IOException and not ObjectDisposedException)
            {
                // Si era un adelanto, deja de valer: cuando la frase se confirme se traduce de nuevo.
                lock (_lock)
                    if (job is not null && _tentative.GetValueOrDefault(job.Tab) == job) _tentative.Remove(job.Tab);
                OnError(ex);
            }
            finally
            {
                lock (_lock)
                {
                    _background = null;
                    _working = false;
                }
            }
        }
    }

    /// <summary>Con atraso, las frases siguientes de la misma pestaña que ya están confirmadas se suman a esta.</summary>
    private void Merge(Job job)
    {
        while (_queue.First is { Value: { Confirmed: true } next } && next.Tab == job.Tab
               && job.Phrase.Samples.Length + next.Phrase.Samples.Length <= MaxMergedSamples)
        {
            _queue.RemoveFirst();
            var joined = new float[job.Phrase.Samples.Length + next.Phrase.Samples.Length];
            job.Phrase.Samples.CopyTo(joined, 0);
            next.Phrase.Samples.CopyTo(joined, job.Phrase.Samples.Length);
            job.Phrase = next.Phrase with { Samples = joined, SpeechMs = job.Phrase.SpeechMs + next.Phrase.SpeechMs };
            job.Merged += next.Merged;
        }
    }

    private void Process(Job job)
    {
        var interpreter = Interpreter;
        var cancel = job.Cancel.Token;
        var t0 = Stopwatch.GetTimestamp();
        var queueMs = (int)Stopwatch.GetElapsedTime(job.Created, t0).TotalMilliseconds; // lo que esperó a que el hilo se desocupara
        string english;
        string? spanish = null;
        int asrMs, llmMs = 0, ttsMs = 0, totalMs = 0;
        var spokenClauses = 0;
        var pendingClauses = new List<string>();
        var catchUp = false;
        IReadOnlyList<(string Es, string En)> hints = [];
        (string Text, double AtMs) heard = default;
        var interpreted = false;

        // Un tramo en inglés listo para decir. Nada suena hasta que la pausa confirme que la frase terminó.
        void Say(string clause)
        {
            var t = Stopwatch.GetTimestamp();
            var pcm = voice.Speak(clause, catchUp ? CatchUpSpeed : 1f);
            if (spokenClauses++ == 0)
            {
                ttsMs = (int)Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                totalMs = (int)(NowMs() - job.Phrase.LastVoiceMs);
            }
            OnAudio(job.Tab, voice.Rate, pcm);
        }

        void Release()
        {
            foreach (var clause in pendingClauses) Say(clause);
            pendingClauses.Clear();
        }

        try
        {
            if (interpreter is null)
            {
                english = engine.Translate(job.Phrase.Samples, glossary?.Prompt, cancel);
                asrMs = (int)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            else
            {
                spanish = engine.Recognize(job.Phrase.Samples, Engine.Mode.Spanish, glossary?.Prompt, cancel);
                asrMs = (int)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (spanish.Length == 0)
                {
                    english = "";
                }
                else
                {
                    hints = Phrasebook?.Hints(spanish) ?? [];
                    lock (_lock)
                    {
                        if (_heard.TryGetValue(job.Tab, out heard) && NowMs() - heard.AtMs > HeardFreshMs) heard = default;
                    }
                    var t1 = Stopwatch.GetTimestamp();
                    try
                    {
                        english = interpreter.Interpret(spanish, heard.Text, hints, clause =>
                        {
                            if (llmMs == 0) llmMs = Math.Max(1, (int)Stopwatch.GetElapsedTime(t1).TotalMilliseconds);
                            if (PhraseText.Clean(clause) is not { Length: > 0 } clean || IsEcho(clean)) return;
                            pendingClauses.Add(clean);
                            bool confirmed;
                            lock (_lock)
                            {
                                confirmed = job.Confirmed;
                                catchUp = job.Merged > 1 || _queue.Any(j => j.Confirmed);
                            }
                            if (confirmed) Release();
                        }, cancel);
                        interpreted = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // El modelo de lenguaje no pudo con esta frase: no se pierde, la traduce Whisper.
                        pendingClauses.Clear();
                        english = spokenClauses > 0 ? "" : engine.Translate(job.Phrase.Samples, glossary?.Prompt, cancel);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // era un adelanto y siguió hablando (el intérprete ya olvidó la frase)
        }

        int pending;
        bool learn;
        lock (_lock)
        {
            // El adelanto está listo antes de que la pausa confirme el fin de la frase: se espera esa confirmación.
            while (!job.Confirmed && !job.Withdrawn && !_stop) Monitor.Wait(_lock);
            if (!job.Confirmed)
            {
                if (interpreted) interpreter!.Rollback();
                return;
            }
            pending = _queue.Count(j => j.Confirmed);
            catchUp = pending > 0 || job.Merged > 1;
            learn = !_noLearn.Contains(job.Tab);
            if (interpreted && heard.Text is not null) _heard.Remove(job.Tab); // ese contexto ya se usó
        }
        if (interpreted) interpreter!.Commit();

        var phrase = job.Phrase;
        if (interpreted) Release();
        else
            foreach (var sentence in PhraseText.Sentences(english)) Say(sentence);
        _done += job.Merged;
        OnDone(new Result(new VozPhrase(job.Tab, Spoken: spokenClauses > 0, phrase.Reason,
            AudioMs: phrase.Samples.Length * 1000 / Engine.Rate, asrMs, ttsMs, totalMs,
            job.Merged, pending, english.Length, job.Speculative, interpreted, llmMs, hints.Count, heard.Text is not null, queueMs),
            phrase, english, spanish));

        if (!learn || english.Length == 0) return;
        if (interpreted && hints.Count > 0) Notes?.Said(spanish!, english);
        if (glossary is null || learnedPath is null) return;
        glossary.Learn(english, DateOnly.FromDateTime(DateTime.Now).DayNumber);
        glossary.SaveLearned(learnedPath);
    }

    /// <summary>El modelo repitió una etiqueta del pedido en vez de traducir: eso no se dice.</summary>
    private static bool IsEcho(string clause) =>
        clause.StartsWith("Spanish:", StringComparison.OrdinalIgnoreCase) || clause.StartsWith("Context:", StringComparison.OrdinalIgnoreCase)
        || clause.StartsWith("Hint:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Escribe lo que dijo otra persona (inglés): contexto para tu próxima frase, términos y apuntes.</summary>
    private void Listen(int tab, Phrase phrase, CancellationToken cancel)
    {
        var text = engine.Recognize(phrase.Samples, Engine.Mode.English, glossary?.Prompt, cancel, HeardThreads);
        _heardDone++;
        if (text.Length == 0) return;
        bool learn;
        lock (_lock)
        {
            // Si venían hablando seguido, el contexto junta lo último que se dijo (hasta el tope).
            var joined = _heard.TryGetValue(tab, out var before) && NowMs() - before.AtMs < HeardFreshMs ? before.Text + " " + text : text;
            if (joined.Length > MaxHeardChars)
            {
                joined = joined[^MaxHeardChars..];
                var space = joined.IndexOf(' ');
                if (space >= 0) joined = joined[(space + 1)..];
            }
            _heard[tab] = (joined, NowMs());
            learn = !_noLearn.Contains(tab);
        }
        if (!learn) return;
        Notes?.Heard(text);
        if (glossary is null || learnedPath is null) return;
        glossary.Learn(text, DateOnly.FromDateTime(DateTime.Now).DayNumber);
        glossary.SaveLearned(learnedPath);
    }

    /// <summary>Elige con el modelo las expresiones de la reunión que vale la pena aprender y las suma al archivo del día.</summary>
    private void SaveNotes(CancellationToken cancel)
    {
        if (Notes is not { } notes || NotesDir is null || !notes.WorthSaving)
        {
            lock (_lock) _notesDue = false;
            return;
        }
        IReadOnlyList<(string Expression, string Meaning)> expressions = [];
        if (Interpreter is { } interpreter && notes.AskPrompt() is { } prompt)
        {
            var answer = interpreter.Ask(MeetingNotes.AskSystem, prompt, maxTokens: 220, cancel); // si hablás, se corta y queda para después
            OnNotesAnswer?.Invoke(answer);
            expressions = notes.ParseAnswer(answer);
        }
        lock (_lock) _notesDue = false;
        var block = notes.Render(DateTime.Now, expressions);
        if (block.Length == 0) return;
        try
        {
            Directory.CreateDirectory(NotesDir);
            var file = Path.Combine(NotesDir, $"{DateTime.Now:yyyy-MM-dd}.md");
            if (!File.Exists(file))
                block = $"# Expresiones de las reuniones del {DateTime.Now:d/M/yyyy}\n\n" +
                        "Las eligió y explicó un modelo de lenguaje chico: el inglés es lo que se dijo; el significado puede estar mal.\n" +
                        "Las que te sirvan, copialas a voz-expresiones.txt (español = inglés) y el intérprete las usa.\n\n" + block;
            File.AppendAllText(file, block);
            OnNotes?.Invoke(expressions.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
