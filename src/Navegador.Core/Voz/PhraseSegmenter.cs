namespace Navegador.Core.Voz;

/// <summary>Una frase cortada por el segmentador, lista para traducir.</summary>
/// <param name="Samples">Audio a 16 kHz, de -1 a 1.</param>
/// <param name="Reason">"pausa" (te callaste), "larga" (pasó el máximo) o "fin" (se cerró el micrófono).</param>
/// <param name="LastVoiceMs">Reloj (el que recibe <see cref="PhraseSegmenter.Push"/>) de tu última palabra.</param>
public sealed record Phrase(float[] Samples, string Reason, double LastVoiceMs, int SpeechMs);

public sealed record SegmenterOptions
{
    /// <summary>Silencio que corta una frase (450 ms dio mejor traducción que cortar por palabras; fase 7).</summary>
    public int PauseMs { get; init; } = 450;
    /// <summary>Una frase más larga se corta igual, para no acumular demora.</summary>
    public int MaxPhraseMs { get; init; } = 8000;
    /// <summary>Menos voz que esto es ruido, no una frase.</summary>
    public int MinSpeechMs { get; init; } = 300;
    /// <summary>Audio de antes de la primera ventana con voz que se conserva (el detector avisa un poco tarde).</summary>
    public int PreRollMs { get; init; } = 192;
    /// <summary>Silencio que se deja al final de la frase; el resto de la pausa no se le pasa al reconocedor.</summary>
    public int TailMs { get; init; } = 160;
}

/// <summary>
/// Corta la voz que llega del micrófono en frases, por las pausas. Si hay voz o no en cada ventana de 32 ms lo decide
/// el detector que se le pasa (Silero en astro-voz.exe); acá sólo está la lógica, que es pura y se prueba sola.
/// Un segmentador por pestaña. No es seguro entre hilos.
/// </summary>
public sealed class PhraseSegmenter(Func<float[], bool> isSpeech, SegmenterOptions? options = null)
{
    public const int Rate = 16000;
    /// <summary>Ventana del detector Silero a 16 kHz: 512 muestras = 32 ms.</summary>
    public const int Window = 512;
    private const int WindowMs = Window * 1000 / Rate;

    private readonly SegmenterOptions _o = options ?? new();
    private readonly float[] _window = new float[Window];
    private int _fill;
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float[]> _phrase = [];
    private bool _active;
    private int _speechMs, _silenceMs;
    private double _lastVoiceMs;

    /// <summary>Hay una frase en curso (voz detectada y todavía sin cortar).</summary>
    public bool Active => _active;

    /// <summary>Voz del micrófono: PCM de 16 bits a 16 kHz, en pedazos de cualquier tamaño.</summary>
    /// <param name="nowMs">Reloj monótono en ms del momento en que llegó el pedazo.</param>
    public void Push(ReadOnlySpan<byte> pcm16, double nowMs, Action<Phrase> onPhrase)
    {
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            _window[_fill++] = (short)(pcm16[i] | (pcm16[i + 1] << 8)) / 32768f;
            if (_fill < Window) continue;
            _fill = 0;
            OnWindow(nowMs, onPhrase);
        }
    }

    /// <summary>Se cerró el micrófono: lo que haya de frase sale ya.</summary>
    public void Flush(Action<Phrase> onPhrase)
    {
        if (_active) Emit("fin", onPhrase);
        Reset();
    }

    private void OnWindow(double nowMs, Action<Phrase> onPhrase)
    {
        var voice = isSpeech(_window);
        var copy = (float[])_window.Clone();
        if (voice)
        {
            if (!_active)
            {
                _active = true;
                _speechMs = 0;
                _phrase.AddRange(_preRoll);
                _preRoll.Clear();
            }
            _speechMs += WindowMs;
            _silenceMs = 0;
            _lastVoiceMs = nowMs;
        }
        else if (_active)
        {
            _silenceMs += WindowMs;
        }

        if (!_active)
        {
            _preRoll.Enqueue(copy);
            while (_preRoll.Count * WindowMs > _o.PreRollMs) _preRoll.Dequeue();
            return;
        }
        _phrase.Add(copy);
        if (_silenceMs >= _o.PauseMs) Emit("pausa", onPhrase);
        else if (_phrase.Count * WindowMs >= _o.MaxPhraseMs) Emit("larga", onPhrase);
    }

    private void Emit(string reason, Action<Phrase> onPhrase)
    {
        var speechMs = _speechMs;
        // De la pausa final sólo queda TailMs: el resto es silencio que el reconocedor no necesita.
        var drop = Math.Max(0, _silenceMs - _o.TailMs) / WindowMs;
        var count = _phrase.Count - drop;
        var samples = new float[Math.Max(0, count) * Window];
        for (var i = 0; i < count; i++) _phrase[i].CopyTo(samples, i * Window);
        var lastVoice = _lastVoiceMs;
        Reset();
        if (speechMs >= _o.MinSpeechMs) onPhrase(new Phrase(samples, reason, lastVoice, speechMs));
    }

    private void Reset()
    {
        _phrase.Clear();
        _preRoll.Clear();
        _active = false;
        _speechMs = _silenceMs = 0;
    }
}
