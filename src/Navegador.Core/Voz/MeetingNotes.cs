using System.Text;

namespace Navegador.Core.Voz;

/// <summary>
/// Apuntes de una reunión para aprender de ella: las expresiones en inglés que usaron los demás (las elige el modelo de
/// lenguaje al terminar, y sólo valen las que de verdad se dijeron) y cómo se dijeron en inglés tus modismos.
/// En memoria queda lo último que se oyó (hasta <see cref="MaxHeard"/> frases); al disco van sólo las expresiones
/// elegidas, nunca la conversación. Acá está la lógica pura; quién consulta al modelo y escribe el archivo es
/// astro-voz.exe. No es seguro entre hilos.
/// </summary>
public sealed class MeetingNotes
{
    public const int MaxHeard = 60, MaxSaid = 40;
    /// <summary>Con menos frases oídas que esto no vale la pena consultar al modelo.</summary>
    public const int MinHeardToAsk = 4;
    public const int MaxExpressions = 8;
    /// <summary>Tope del texto que se le pasa al modelo (tiene que caber en su memoria).</summary>
    private const int MaxPromptChars = 2400;

    private readonly List<string> _heard = [];
    private readonly List<(string Es, string En)> _said = [];

    public int HeardCount => _heard.Count;
    public bool Empty => _heard.Count == 0 && _said.Count == 0;
    /// <summary>Hay algo para anotar: suficientes frases oídas para elegir expresiones, o modismos tuyos dichos.</summary>
    public bool WorthSaving => _heard.Count >= MinHeardToAsk || _said.Count > 0;

    /// <summary>Lo que dijo otra persona, en inglés.</summary>
    public void Heard(string english)
    {
        if (english.Length < 12) return; // "Yeah.", "Okay." no enseñan nada
        _heard.Add(english);
        if (_heard.Count > MaxHeard) _heard.RemoveAt(0);
    }

    /// <summary>Un modismo tuyo y cómo se dijo en inglés (sólo las frases que usaron el libro de frases).</summary>
    public void Said(string spanish, string english)
    {
        if (_said.Any(s => s.Es == spanish)) return;
        _said.Add((spanish, english));
        if (_said.Count > MaxSaid) _said.RemoveAt(0);
    }

    public const string AskSystem = "You help a Spanish speaker learn the English that native speakers use in his business meetings.";

    /// <summary>La consulta al modelo: que elija las expresiones que vale la pena aprender de lo oído. null si no alcanza.</summary>
    public string? AskPrompt()
    {
        if (_heard.Count < MinHeardToAsk) return null;
        // Lo más reciente que entre en el tope, en el orden en que se dijo.
        var first = _heard.Count;
        for (var chars = 0; first > 0 && chars + _heard[first - 1].Length < MaxPromptChars; first--) chars += _heard[first - 1].Length;
        var lines = new StringBuilder();
        foreach (var line in _heard.Skip(first)) lines.Append("- ").Append(line).Append('\n');
        // Con el formato sólo descrito, el modelo chico devuelve la descripción ("expression = meaning in Spanish"):
        // necesita ejemplos. Si los repite, no pasan el filtro (no se dijeron).
        return "Sentences said in English in a meeting:\n" + lines +
               $"\nFrom those sentences, list up to {MaxExpressions} idioms, phrasal verbs and set phrases (2 to 6 words each) that a " +
               "Spanish speaker should learn, each with its meaning in Spanish. Copy each one exactly as it was said. " +
               "No whole sentences, no single words, no names. One per line, like these examples:\n" +
               "get the ball rolling = empezar\nrun it by you = consultártelo\nat the end of the day = al final";
    }

    /// <summary>
    /// Las expresiones que contestó el modelo, filtradas: formato <c>expresión = significado</c>, de dos palabras o más,
    /// y que aparezcan de verdad en lo oído (un modelo chico inventa; lo que no se dijo no se anota).
    /// </summary>
    public IReadOnlyList<(string Expression, string Meaning)> ParseAnswer(string answer)
    {
        var heard = Phrasebook.Normalize(string.Join(' ', _heard));
        var result = new List<(string, string)>();
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '•', ' ').Trim();
            while (line.Length > 0 && (char.IsDigit(line[0]) || line[0] is '.' or ')')) line = line[1..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var expression = line[..eq].Trim().Trim('"', '“', '”', '*');
            var meaning = line[(eq + 1)..].Trim().Trim('"', '“', '”', '*');
            var key = Phrasebook.Normalize(expression);
            if (meaning.Length == 0 || key.Count(c => c == ' ') is < 1 or > 6) continue; // de 2 a 7 palabras
            if (!(" " + heard + " ").Contains(" " + key + " ", StringComparison.Ordinal)) continue;
            if (result.Any(r => Phrasebook.Normalize(r.Item1) == key)) continue;
            result.Add((expression, meaning));
            if (result.Count == MaxExpressions) break;
        }
        return result;
    }

    /// <summary>El bloque para el archivo del día ("" si no hay nada que anotar), y deja los apuntes vacíos.</summary>
    public string Render(DateTime now, IReadOnlyList<(string Expression, string Meaning)> expressions)
    {
        var text = new StringBuilder();
        if (expressions.Count > 0)
        {
            text.Append("Expresiones que usaron los demás (inglés = qué quiere decir):\n");
            foreach (var (expression, meaning) in expressions) text.Append("- ").Append(expression).Append(" = ").Append(meaning).Append('\n');
        }
        if (_said.Count > 0)
        {
            if (text.Length > 0) text.Append('\n');
            text.Append("Tus expresiones y cómo se dijeron (español = inglés):\n");
            foreach (var (es, en) in _said) text.Append("- ").Append(es).Append(" = ").Append(en).Append('\n');
        }
        _heard.Clear();
        _said.Clear();
        return text.Length == 0 ? "" : $"## {now:HH:mm}\n\n{text}\n";
    }
}
