using System.Diagnostics;
using Navegador.Core.Voz;

namespace Astro.Voz;

/// <summary>
/// El hilo que traduce: una frase a la vez → Whisper → voz inglesa. Lo alimenta el hilo que corta las frases, con tres
/// avisos por pestaña:
///   · <see cref="Tentative"/>: "probablemente terminó la frase" (192 ms de silencio). Se empieza a traducir ya.
///   · <see cref="Withdraw"/>: era una pausa para respirar, siguió hablando. Lo adelantado se corta y se tira.
///   · <see cref="Confirm"/>: la frase terminó. Si es la que se venía adelantando, la traducción ya está hecha o en
///     camino; si no, se traduce ahora.
/// Así la demora no incluye la pausa que confirma el fin de la frase (650 ms): se traduce durante esa pausa.
/// Si las frases se atrasan, las pendientes de la misma pestaña se juntan en una y la voz va un poco más rápido.
/// </summary>
internal sealed class Translator(Engine engine, IVoice voice, Glossary? glossary, string? learnedPath)
{
    /// <summary>Tope de una frase armada juntando atrasadas: más que esto se traduce por separado.</summary>
    private const int MaxMergedSamples = Engine.Rate * 20;
    /// <summary>Ritmo de la voz cuando hay frases esperando.</summary>
    private const float CatchUpSpeed = 1.12f;

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
    }

    /// <summary>Una frase terminada: sus tiempos (lo que va al log) y su texto (que no sale de este proceso).</summary>
    public sealed record Result(VozPhrase Info, Phrase Phrase, string English);

    private readonly object _lock = new();
    private readonly LinkedList<Job> _queue = new();
    private readonly Dictionary<int, Job> _tentative = [];
    private readonly HashSet<int> _noLearn = [];
    private bool _stop;
    private volatile int _phrases, _done;

    /// <summary>Voz traducida lista: pestaña, Hz, PCM. Sale de a oraciones.</summary>
    public required Action<int, int, byte[]> OnAudio { get; init; }
    public required Action<Result> OnDone { get; init; }
    public required Action<Exception> OnError { get; init; }

    /// <summary>Frases confirmadas / terminadas (traducidas, con o sin algo para decir).</summary>
    public int Phrases => _phrases;
    public int Done => _done;

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
            }
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Lo que se diga en esta pestaña no se aprende (ventana privada).</summary>
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
            Job job;
            lock (_lock)
            {
                while (_queue.Count == 0 && !_stop) Monitor.Wait(_lock);
                if (_stop) return;
                job = _queue.First!.Value;
                _queue.RemoveFirst();
                if (job.Confirmed) Merge(job);
            }
            try
            {
                Process(job);
            }
            catch (Exception ex) when (ex is not IOException and not ObjectDisposedException)
            {
                // Si era un adelanto, deja de valer: cuando la frase se confirme se traduce de nuevo.
                lock (_lock)
                    if (_tentative.GetValueOrDefault(job.Tab) == job) _tentative.Remove(job.Tab);
                OnError(ex);
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
        var t0 = Stopwatch.GetTimestamp();
        string english;
        try
        {
            english = engine.Translate(job.Phrase.Samples, glossary?.Prompt, job.Cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return; // era un adelanto y siguió hablando
        }
        var asrMs = (int)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        int pending;
        bool learn;
        lock (_lock)
        {
            // El adelanto está listo antes de que la pausa confirme el fin de la frase: se espera esa confirmación.
            while (!job.Confirmed && !job.Withdrawn && !_stop) Monitor.Wait(_lock);
            if (!job.Confirmed) return;
            pending = _queue.Count(j => j.Confirmed);
            learn = !_noLearn.Contains(job.Tab);
        }

        var phrase = job.Phrase;
        var t1 = Stopwatch.GetTimestamp();
        int totalMs = 0, ttsMs = 0;
        var first = true;
        // De a oraciones: la primera empieza a sonar mientras se arma la voz de las siguientes.
        foreach (var sentence in PhraseText.Sentences(english))
        {
            var pcm = voice.Speak(sentence, pending > 0 || job.Merged > 1 ? CatchUpSpeed : 1f);
            if (first)
            {
                ttsMs = (int)Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                totalMs = (int)(NowMs() - phrase.LastVoiceMs);
                first = false;
            }
            OnAudio(job.Tab, voice.Rate, pcm);
        }
        _done += job.Merged;
        OnDone(new Result(new VozPhrase(job.Tab, Spoken: !first, phrase.Reason,
            AudioMs: phrase.Samples.Length * 1000 / Engine.Rate, asrMs, ttsMs, totalMs,
            job.Merged, pending, english.Length, job.Speculative), phrase, english));

        if (glossary is null || learnedPath is null || !learn || english.Length == 0) return;
        glossary.Learn(english, DateOnly.FromDateTime(DateTime.Now).DayNumber);
        glossary.SaveLearned(learnedPath);
    }
}
