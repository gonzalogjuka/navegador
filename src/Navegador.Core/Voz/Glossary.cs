using System.Text;
using System.Text.Json;

namespace Navegador.Core.Voz;

/// <summary>
/// Vocabulario propio del traductor: nombres y términos que el reconocedor no conoce (productos, empresas, personas,
/// siglas). Se le pasan a Whisper como pista antes de cada frase y así los escribe bien: medido el 26/11, sin pista
/// "Supabase", "Vercel" y "Kubernetes" salían "database", "Brazil" y "the government"; con la pista, los tres bien.
/// Dos fuentes:
///   · el glosario que escribe la persona (un término por línea), que siempre entra;
///   · lo aprendido con el uso: los nombres propios y siglas que aparecen en lo que se traduce. Un término entra en la
///     pista cuando apareció en <see cref="PromoteAt"/> frases; los que dejan de usarse se olvidan.
/// De lo aprendido sólo se guardan términos sueltos con su cuenta, nunca frases. No es seguro entre hilos.
/// </summary>
public sealed class Glossary
{
    /// <summary>Frases en las que tiene que aparecer un término para entrar en la pista.</summary>
    public const int PromoteAt = 2;
    /// <summary>Largo máximo de la pista: cada palabra de más son milisegundos de más en cada frase.</summary>
    public const int MaxPromptChars = 240;
    public const int MaxLearned = 300;
    public const int ForgetAfterDays = 90;
    private const int MaxTermWords = 3, MaxTermChars = 40;

    private sealed record Entry(int Count, int LastDay);

    private readonly List<string> _user = [];
    private readonly Dictionary<string, Entry> _learned = new(StringComparer.Ordinal);
    private string? _prompt;

    /// <summary>Cambió lo aprendido desde la última vez que se guardó.</summary>
    public bool Dirty { get; private set; }

    public IReadOnlyList<string> UserTerms => _user;

    /// <summary>Términos aprendidos que ya entran en la pista.</summary>
    public IEnumerable<string> LearnedTerms => Ranked().Select(e => e.Key);

    /// <summary>Glosario de la persona: un término por línea; las líneas vacías y las que empiezan con # no cuentan.</summary>
    public void SetUserTerms(IEnumerable<string> lines)
    {
        _user.Clear();
        foreach (var line in lines)
        {
            var term = line.Trim();
            if (term.Length is 0 or > MaxTermChars || term[0] == '#') continue;
            if (!_user.Contains(term, StringComparer.OrdinalIgnoreCase)) _user.Add(term);
        }
        _prompt = null;
    }

    /// <summary>La pista para Whisper ("" si no hay términos): primero el glosario, después lo más usado.</summary>
    public string Prompt
    {
        get
        {
            if (_prompt is not null) return _prompt;
            var text = new StringBuilder();
            foreach (var term in _user.Concat(LearnedTerms.Where(t => !_user.Contains(t, StringComparer.OrdinalIgnoreCase))))
            {
                if (text.Length + term.Length + 2 > MaxPromptChars) break;
                text.Append(text.Length == 0 ? "Glossary: " : ", ").Append(term);
            }
            return _prompt = text.Length == 0 ? "" : text.Append('.').ToString();
        }
    }

    private IEnumerable<KeyValuePair<string, Entry>> Ranked() => _learned
        .Where(e => e.Value.Count >= PromoteAt)
        .OrderByDescending(e => e.Value.Count).ThenByDescending(e => e.Value.LastDay).ThenBy(e => e.Key, StringComparer.Ordinal);

    /// <summary>Anota los nombres propios y siglas de una frase ya traducida.</summary>
    /// <param name="day">Día de hoy (número de día), para olvidar lo que deja de usarse.</param>
    public void Learn(string english, int day)
    {
        foreach (var term in Terms(english).Distinct(StringComparer.Ordinal))
        {
            _learned[term] = new Entry((_learned.GetValueOrDefault(term)?.Count ?? 0) + 1, day);
            Dirty = true;
            _prompt = null;
        }
        if (_learned.Count <= MaxLearned) return;
        foreach (var old in _learned.OrderBy(e => e.Value.Count).ThenBy(e => e.Value.LastDay).Take(_learned.Count - MaxLearned).ToList())
            _learned.Remove(old.Key);
    }

    /// <summary>
    /// Nombres propios y siglas de un texto en inglés: palabras con mayúscula que no abren la oración ("Vercel"), con
    /// mayúsculas adentro ("GitHub") o todas en mayúsculas ("AWS"); las seguidas se juntan ("Control Cloud"). Una palabra
    /// que abre la oración sólo cuenta si ya se la conoce.
    /// </summary>
    internal IEnumerable<string> Terms(string english)
    {
        var run = new List<string>();
        var held = false; // el término en curso es una sola palabra que abre la oración: vale sólo si sigue otro nombre
        var sentenceStart = true;
        foreach (var raw in english.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('"', '\'', '(', ')', '[', ']', ',', ';', ':', '.', '!', '?', '…', '-', '¿', '¡');
            if (word.EndsWith("'s", StringComparison.Ordinal)) word = word[..^2];
            var nameLike = IsNameLike(word);
            if (nameLike && run.Count is > 0 and < MaxTermWords)
            {
                run.Add(word);
                held = false;
            }
            else
            {
                if (run.Count > 0 && !held) yield return string.Join(' ', run);
                run.Clear();
                if (nameLike) run.Add(word);
                held = nameLike && sentenceStart && !Shouts(word) && !_learned.ContainsKey(word);
            }
            // Una coma o el fin de la oración cortan el término: "in Vercel, Supabase" son dos.
            var last = raw[^1];
            if (last is ',' or ';' or ':' or '.' or '!' or '?')
            {
                if (run.Count > 0 && !held) yield return string.Join(' ', run);
                run.Clear();
            }
            sentenceStart = last is '.' or '!' or '?';
        }
        if (run.Count > 0 && !held) yield return string.Join(' ', run);
    }

    private static bool IsNameLike(string word)
    {
        if (word.Length < 2 || word.Length > MaxTermChars || !char.IsUpper(word[0]) || Common.Contains(word)) return false;
        return word.All(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '+' or '#');
    }

    /// <summary>Sigla o nombre con mayúsculas adentro: es un nombre aunque abra la oración ("AWS", "GitHub").</summary>
    private static bool Shouts(string word) => word.Skip(1).Any(char.IsUpper);

    /// <summary>Palabras que el inglés escribe con mayúscula sin ser vocabulario propio.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "I", "I'm", "I'll", "I've", "I'd", "OK", "Okay", "Mr", "Mrs", "Ms", "Dr", "God", "TV", "PM", "AM",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December",
        "English", "Spanish", "Portuguese", "American", "Internet", "Christmas",
        // Palabras que suelen abrir una oración (ahí van con mayúscula sin ser nombres).
        "The", "A", "An", "We", "You", "He", "She", "It", "They", "This", "That", "These", "Those", "There", "Here",
        "If", "So", "And", "But", "Or", "Then", "Because", "When", "Where", "Why", "How", "What", "Who", "Which",
        "Do", "Does", "Did", "Is", "Are", "Was", "Were", "Can", "Could", "Will", "Would", "Should", "Have", "Has",
        "Let's", "Let", "Our", "My", "Your", "His", "Her", "Their", "Its", "In", "On", "At", "For", "With", "From",
        "To", "Of", "As", "By", "About", "After", "Before", "Now", "Today", "Tomorrow", "Yesterday", "Last", "Next",
        "Yes", "No", "Not", "Hello", "Hi", "Hey", "Thanks", "Thank", "Please", "Sorry", "Well", "Perfect", "Great",
        "Good", "Sure", "Right", "Also", "Maybe", "Just", "Some", "Someone", "Anyone", "Everyone", "All", "One",
        "We're", "We'll", "We've", "You're", "It's", "That's", "There's", "What's", "Don't", "Can't", "Go", "See",
    };

    // ---------- Disco ----------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Carga lo aprendido (JSON: término → cuenta y último día). Un archivo roto se ignora: se vuelve a aprender.</summary>
    public void LoadLearned(string path, int day)
    {
        try
        {
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), Json);
            if (saved is null) return;
            foreach (var (term, entry) in saved)
                if (term.Length is > 1 and <= MaxTermChars && entry.Count > 0 && day - entry.LastDay <= ForgetAfterDays)
                    _learned[term] = entry;
            _prompt = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void SaveLearned(string path)
    {
        if (!Dirty) return;
        try
        {
            var ordered = _learned.OrderByDescending(e => e.Value.Count).ThenBy(e => e.Key, StringComparer.Ordinal)
                .ToDictionary(e => e.Key, e => e.Value);
            var partial = path + ".tmp";
            File.WriteAllText(partial, JsonSerializer.Serialize(ordered, Json), new UTF8Encoding(false));
            File.Move(partial, path, overwrite: true);
            Dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public const string UserFileHeader =
        "# Glosario del traductor de Astro: nombres y términos que querés que escriba y diga bien en inglés\n" +
        "# (productos, empresas, personas, siglas). Uno por línea, escrito como va en inglés. Ejemplos:\n" +
        "# Control Cloud\n" +
        "# Kubernetes\n";

    /// <summary>Lee el glosario de la persona; si no existe, deja el archivo con las instrucciones.</summary>
    public void LoadUserFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, UserFileHeader, new UTF8Encoding(false));
            SetUserTerms(File.ReadAllLines(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
