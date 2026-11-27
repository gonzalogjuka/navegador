using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;

namespace Astro.Voz;

/// <summary>
/// El intérprete: un modelo de lenguaje chico, local y sólo con el procesador (Qwen2.5-1.5B-Instruct Q4_K_M, con
/// llama.cpp) que recibe lo que dijiste en español, ya escrito por Whisper, y lo dice en inglés como lo diría un nativo
/// en una reunión: por el sentido, no palabra por palabra, mirando lo que se viene hablando.
/// Medido el 26/11 (i5-9600K, 5 hilos): primer tramo en ~0,8 s, ~25 tokens por segundo, ~1,1 GB de memoria. Contra la
/// traducción directa de Whisper: "salir a producción" → "go live" (antes "leave production"), "pruebas de carga" →
/// "load testing" ("the charges test"), "nos estamos quedando cortos" → "we're running short" ("getting short").
/// Probados y descartados: Qwen 0.5B (3 veces más rápido pero cambia el sentido), Gemma 2 2B y Llama 3.2 3B (mejores
/// con los modismos, pero 2 a 3 veces más lentos y 1,7 a 1,9 GB).
/// La conversación queda en la memoria del modelo (cada frase se suma a las anteriores); las instrucciones se evalúan
/// una sola vez. Un hilo a la vez.
/// </summary>
internal sealed class Interpreter : IDisposable
{
    private const int ContextSize = 2048;
    /// <summary>Pasado esto, la conversación se olvida y se vuelve a las instrucciones (no cabe más).</summary>
    private const int ForgetAt = 1400;

    private const string Instructions =
        "You are a simultaneous interpreter in a business video call. The speaker talks in Argentine Spanish. " +
        "Say what he means in natural, idiomatic American English, the way a native speaker would say it in a meeting. " +
        "Convey the meaning, not word by word; use the usual English expression for each Spanish idiom. Keep it as short as the original. " +
        "Keep names and technical terms as they are. The Spanish comes from speech recognition and may contain small errors: use the context. " +
        "Each message has an optional \"Context:\" line (what the other people in the call just said in English; it is only background, " +
        "never translate it or answer it), an optional \"Hint:\" line (the expression to use for an idiom) and a \"Spanish:\" line. " +
        "Translate only the Spanish line. Output only the English sentence, nothing else.";

    /// <summary>Ejemplos que fijan el formato y el tono (con y sin contexto y pista).</summary>
    private static readonly (string User, string Assistant)[] Shots =
    [
        ("Spanish: Bueno, arranquemos.", "Alright, let's get started."),
        ("Hint: se nos fue de las manos = it got out of hand\nSpanish: La verdad que se nos fue de las manos.", "Honestly, it got out of hand."),
        ("Context: Did you get a chance to look at the proposal?\nSpanish: Sí, lo vi ayer, está bárbaro.", "Yes, I looked at it yesterday, it looks great."),
        ("Spanish: Dale, quedamos así.", "Sounds good, let's leave it at that."),
    ];

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly LLamaToken[] _stop;
    private string _instructions = "";
    private int _prefix, _past, _turnStart;

    private Interpreter(LLamaWeights weights, LLamaContext context)
    {
        _weights = weights;
        _context = context;
        _stop = [.. context.Tokenize("<|im_end|>", addBos: false, special: true), .. context.Tokenize("<|endoftext|>", addBos: false, special: true)];
    }

    /// <param name="glossary">Nombres y términos propios, separados por coma ("" si no hay): van en las instrucciones.</param>
    public static Interpreter Load(string modelPath, int threads, string glossary)
    {
        NativeLibraryConfig.All.WithLogCallback((_, _) => { }); // llama.cpp escribe en la consola; acá no hay
        var parameters = new ModelParams(modelPath)
        {
            ContextSize = ContextSize, BatchSize = 512, Threads = threads, BatchThreads = threads, GpuLayerCount = 0,
        };
        var weights = LLamaWeights.LoadFromFile(parameters);
        var interpreter = new Interpreter(weights, weights.CreateContext(parameters));
        var prefix = new StringBuilder("<|im_start|>system\n").Append(Instructions);
        if (glossary.Length > 0) prefix.Append("\nGlossary: ").Append(glossary).Append('.');
        prefix.Append("<|im_end|>\n");
        foreach (var (user, assistant) in Shots)
            prefix.Append("<|im_start|>user\n").Append(user).Append("<|im_end|>\n<|im_start|>assistant\n").Append(assistant).Append("<|im_end|>\n");
        interpreter._instructions = prefix.ToString();
        interpreter.LoadInstructions();
        // Precalentar: la primera generación reserva memoria y tarda varias veces más.
        interpreter.Interpret("Hola, ¿me escuchan bien?", null, [], _ => { }, CancellationToken.None);
        interpreter.Rollback();
        return interpreter;
    }

    /// <summary>
    /// Dice en inglés una frase en español. El resultado sale de a tramos (hasta una coma o un punto) por
    /// <paramref name="onClause"/>, apenas están, y entero como valor de retorno. Después hay que llamar a
    /// <see cref="Commit"/> (la frase queda en la conversación) o a <see cref="Rollback"/> (no se dijo: se olvida).
    /// </summary>
    /// <param name="heard">Lo último que dijeron los demás en inglés, o null.</param>
    /// <param name="hints">Expresiones del libro de frases que aplican a esta frase.</param>
    /// <exception cref="OperationCanceledException">Se cortó por <paramref name="cancel"/> (ya se hizo el Rollback).</exception>
    public string Interpret(string spanish, string? heard, IReadOnlyList<(string Es, string En)> hints, Action<string> onClause,
        CancellationToken cancel)
    {
        _turnStart = _past;
        var turn = new StringBuilder("<|im_start|>user\n");
        if (!string.IsNullOrWhiteSpace(heard)) turn.Append("Context: ").Append(OneLine(heard)).Append('\n');
        foreach (var (es, en) in hints) turn.Append("Hint: ").Append(es).Append(" = ").Append(en).Append('\n');
        turn.Append("Spanish: ").Append(OneLine(spanish)).Append("<|im_end|>\n<|im_start|>assistant\n");
        try
        {
            Evaluate(turn.ToString(), cancel, wantLogits: true);
            // Una traducción no es mucho más larga que el original: tope contra bucles y divagues.
            var maxTokens = Math.Min(160, spanish.Length / 2 + 24);
            var text = Generate(maxTokens, stopAtNewline: true, onClause, cancel);
            Evaluate("<|im_end|>\n", cancel, wantLogits: false);
            return text;
        }
        catch
        {
            Rollback(); // cortada o fallida, la frase no queda a medias en la memoria del modelo
            throw;
        }
    }

    /// <summary>La última frase se dijo: queda en la conversación (o se empieza de nuevo si ya no cabe).</summary>
    public void Commit()
    {
        _turnStart = _past;
        if (_past > ForgetAt) Forget(_prefix);
    }

    /// <summary>La última frase no se dijo (era un adelanto y siguió hablando): el modelo la olvida.</summary>
    public void Rollback() => Forget(_turnStart);

    /// <summary>
    /// Una consulta suelta al modelo (por ejemplo, elegir las expresiones de una reunión), fuera de la conversación:
    /// no ve lo anterior y, al terminar, la conversación empieza de nuevo (vuelven a evaluarse las instrucciones, ~1 s).
    /// </summary>
    public string Ask(string system, string user, int maxTokens, CancellationToken cancel)
    {
        Forget(0); // la consulta necesita todo el lugar
        try
        {
            Evaluate($"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n", cancel, wantLogits: true);
            return Generate(maxTokens, stopAtNewline: false, _ => { }, cancel);
        }
        finally
        {
            LoadInstructions();
        }
    }

    /// <summary>Deja en la memoria del modelo sólo las instrucciones y los ejemplos.</summary>
    private void LoadInstructions()
    {
        Forget(0);
        Evaluate(_instructions, CancellationToken.None, wantLogits: false);
        _prefix = _turnStart = _past;
    }

    private void Forget(int from)
    {
        if (from >= _past) return;
        _context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, from, -1);
        _past = from;
        if (_turnStart > from) _turnStart = from;
    }

    private void Evaluate(string text, CancellationToken cancel, bool wantLogits)
    {
        var tokens = _context.Tokenize(text, addBos: false, special: true);
        if (_past + tokens.Length >= ContextSize - 8) throw new InvalidOperationException("la conversación no cabe en el modelo");
        var batch = new LLamaBatch();
        for (var i = 0; i < tokens.Length;)
        {
            cancel.ThrowIfCancellationRequested();
            batch.Clear();
            var n = Math.Min(64, tokens.Length - i); // de a pedazos chicos: se puede cortar en ~100 ms
            for (var k = 0; k < n; k++)
                batch.Add(tokens[i + k], _past + k, LLamaSeqId.Zero, wantLogits && i + k == tokens.Length - 1);
            Decode(batch);
            _past += n;
            i += n;
        }
    }

    private string Generate(int maxTokens, bool stopAtNewline, Action<string> onClause, CancellationToken cancel)
    {
        var decoder = new StreamingTokenDecoder(_context);
        var all = new StringBuilder();
        var clause = new StringBuilder();
        var batch = new LLamaBatch();
        var minClause = 20; // el primer tramo sale antes (es lo que define la demora); los siguientes, más enteros
        for (var produced = 0; produced < maxTokens; produced++)
        {
            cancel.ThrowIfCancellationRequested();
            var token = Greedy(_context.NativeHandle.GetLogitsIth(-1));
            if (Array.IndexOf(_stop, token) >= 0) break;
            decoder.Add(token);
            var piece = decoder.Read();
            var newline = stopAtNewline ? piece.IndexOf('\n') : -1;
            if (newline >= 0) piece = piece[..newline];
            all.Append(piece);
            // Una oración larga sin comas no espera al punto: pasados los 70 caracteres sale hasta la última palabra entera.
            if (clause.Length >= 70 && piece.StartsWith(' '))
            {
                onClause(clause.ToString().Trim());
                clause.Clear();
                minClause = 40;
            }
            clause.Append(piece);
            if (newline >= 0) break;
            // Un tramo listo: termina en coma o punto y ya tiene un largo que vale la pena decir.
            if (clause.Length >= minClause && piece.Length > 0 && piece[^1] is ',' or '.' or ';' or ':' or '!' or '?')
            {
                onClause(clause.ToString().Trim());
                clause.Clear();
                minClause = 40;
            }
            batch.Clear();
            batch.Add(token, _past, LLamaSeqId.Zero, true);
            Decode(batch);
            _past++;
        }
        if (clause.ToString().Trim() is { Length: > 0 } rest) onClause(rest);
        return all.ToString().Trim();
    }

    private void Decode(LLamaBatch batch)
    {
        var result = _context.Decode(batch);
        if (result != DecodeResult.Ok) throw new InvalidOperationException("el modelo de lenguaje no pudo seguir: " + result);
    }

    private static LLamaToken Greedy(Span<float> logits)
    {
        var best = 0;
        for (var i = 1; i < logits.Length; i++)
            if (logits[i] > logits[best]) best = i;
        return best;
    }

    private static string OneLine(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Trim();

    public void Dispose()
    {
        _context.Dispose();
        _weights.Dispose();
    }
}
