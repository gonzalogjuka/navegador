namespace Navegador.Core.Voz;

/// <summary>Una frase cortada por el segmentador, lista para traducir.</summary>
/// <param name="Samples">Audio a 16 kHz, de -1 a 1.</param>
/// <param name="Reason">"pausa" (te callaste), "larga" (pasó el máximo) o "fin" (se cerró el micrófono).</param>
/// <param name="LastVoiceMs">Reloj (el que recibe <see cref="PhraseSegmenter.Push"/>) de tu última palabra.</param>
public sealed record Phrase(float[] Samples, string Reason, double LastVoiceMs, int SpeechMs);

/// <summary>
/// Tiempos del segmentador, en milisegundos de audio real. Los de la 0.7.0 se habían elegido con el audio duplicado
/// (cada captura del micrófono mandaba su copia: el reloj corría al doble), así que valían la mitad de lo que decían.
/// </summary>
public sealed record SegmenterOptions
{
    /// <summary>
    /// Silencio que corta una frase. No suma demora: la traducción arranca antes, a los <see cref="TentativeMs"/> de
    /// silencio, y tarda más que lo que falta para llegar a esta pausa.
    /// </summary>
    public int PauseMs { get; init; } = 650;
    /// <summary>
    /// Silencio a partir del cual se avisa "probablemente terminó la frase" para ir traduciendo. Las pausas entre
    /// palabras duran menos; si era una pausa para respirar y seguís hablando, ese adelanto se descarta (cortarlo
    /// cuesta ~15 ms). No puede ser menor que <see cref="TailMs"/>.
    /// </summary>
    public int TentativeMs { get; init; } = 128;
    /// <summary>Una frase que ya dura esto se corta en la primera pausa corta (<see cref="LongPauseMs"/>).</summary>
    public int LongPhraseMs { get; init; } = 5000;
    /// <summary>Pausa que corta una frase que ya es larga: quien te escucha no espera al final del párrafo.</summary>
    public int LongPauseMs { get; init; } = 320;
    /// <summary>Hablando sin ninguna pausa, la frase se corta acá igual, para no acumular demora.</summary>
    public int MaxPhraseMs { get; init; } = 10000;
    /// <summary>Menos voz que esto es ruido, no una frase.</summary>
    public int MinSpeechMs { get; init; } = 300;
    /// <summary>
    /// Una frase con menos voz que esto no sale sola en la primera pausa: espera a ver si seguís hablando y se junta con
    /// lo que sigue (con muy poco audio el reconocedor adivina).
    /// </summary>
    public int MinPhraseMs { get; init; } = 500;
    /// <summary>Silencio después del cual una frase corta sale igual, sola ("sí", "ok").</summary>
    public int ShortHoldMs { get; init; } = 1000;
    /// <summary>Audio de antes de la primera ventana con voz que se conserva (el detector avisa un poco tarde).</summary>
    public int PreRollMs { get; init; } = 320;
    /// <summary>Silencio que se deja al final de la frase; el resto de la pausa no se le pasa al reconocedor.</summary>
    public int TailMs { get; init; } = 128;
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
    private bool _active, _tentative;
    private int _speechMs, _silenceMs;
    private double _lastVoiceMs;

    /// <summary>Hay una frase en curso (voz detectada y todavía sin cortar).</summary>
    public bool Active => _active;

    /// <summary>Voz del micrófono: PCM de 16 bits a 16 kHz, en pedazos de cualquier tamaño.</summary>
    /// <param name="nowMs">Reloj monótono en ms del momento en que llegó el pedazo.</param>
    /// <param name="onPhrase">Una frase terminada.</param>
    /// <param name="onTentative">
    /// Adelanto: recibe la frase tal como saldría si el silencio sigue (mismo audio, muestra por muestra), para ir
    /// traduciéndola; o null si después del adelanto volviste a hablar y ya no vale. Después de un adelanto que no se
    /// anuló, la próxima frase que sale es esa misma.
    /// </param>
    public void Push(ReadOnlySpan<byte> pcm16, double nowMs, Action<Phrase> onPhrase, Action<Phrase?>? onTentative = null)
    {
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            _window[_fill++] = (short)(pcm16[i] | (pcm16[i + 1] << 8)) / 32768f;
            if (_fill < Window) continue;
            _fill = 0;
            OnWindow(nowMs, onPhrase, onTentative);
        }
    }

    /// <summary>Se cerró el micrófono: lo que haya de frase sale ya.</summary>
    public void Flush(Action<Phrase> onPhrase)
    {
        if (_active) Emit("fin", onPhrase);
        Reset();
    }

    private void OnWindow(double nowMs, Action<Phrase> onPhrase, Action<Phrase?>? onTentative)
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
            if (_tentative)
            {
                _tentative = false;
                onTentative?.Invoke(null);
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
        var phraseMs = _phrase.Count * WindowMs;
        var pause = phraseMs - _silenceMs >= _o.LongPhraseMs ? _o.LongPauseMs : _o.PauseMs;
        // Una frase corta espera a ver si seguís hablando (se junta con lo que sigue); si no, sale sola igual.
        if (_silenceMs >= pause && (_speechMs >= _o.MinPhraseMs || _silenceMs >= _o.ShortHoldMs)) Emit("pausa", onPhrase);
        else if (phraseMs >= _o.MaxPhraseMs) Emit("larga", onPhrase);
        else if (onTentative is not null && !_tentative && _silenceMs >= _o.TentativeMs && _silenceMs >= _o.TailMs
                 && _speechMs >= _o.MinSpeechMs)
        {
            _tentative = true;
            onTentative(Snapshot("pausa"));
        }
    }

    /// <summary>La frase en curso como saldría ahora. De la pausa final sólo queda TailMs: el resto es silencio que el
    /// reconocedor no necesita (por eso el adelanto y la frase definitiva llevan el mismo audio).</summary>
    private Phrase Snapshot(string reason)
    {
        var drop = Math.Max(0, _silenceMs - _o.TailMs) / WindowMs;
        var count = Math.Max(0, _phrase.Count - drop);
        var samples = new float[count * Window];
        for (var i = 0; i < count; i++) _phrase[i].CopyTo(samples, i * Window);
        return new Phrase(samples, reason, _lastVoiceMs, _speechMs);
    }

    private void Emit(string reason, Action<Phrase> onPhrase)
    {
        var phrase = Snapshot(reason);
        Reset();
        if (phrase.SpeechMs >= _o.MinSpeechMs) onPhrase(phrase);
    }

    private void Reset()
    {
        _phrase.Clear();
        _preRoll.Clear();
        _active = _tentative = false;
        _speechMs = _silenceMs = 0;
    }
}
