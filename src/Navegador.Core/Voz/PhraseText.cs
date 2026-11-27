using System.Text.RegularExpressions;

namespace Navegador.Core.Voz;

/// <summary>Limpieza del texto que devuelve Whisper antes de decirlo en la reunión.</summary>
public static partial class PhraseText
{
    /// <summary>
    /// Texto para decir, o "" si no hay nada: Whisper inventa frases típicas sobre silencio o ruido ("Thank you.",
    /// "Thanks for watching") y anota sonidos entre corchetes o paréntesis ("[BLANK_AUDIO]", "(music)"); a veces entra
    /// en bucle y repite lo mismo ("I'm going to say I'm going to say…"), que se deja una sola vez.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Annotation().Replace(text, " ");
        s = Spaces().Replace(s, " ").Trim();
        if (s.Length == 0 || !s.Any(char.IsLetterOrDigit)) return "";
        if (Hallucination().IsMatch(s)) return "";
        return CollapseLoops(s);
    }

    /// <summary>
    /// El texto en tramos (oraciones, o partes de una oración hasta una coma), para ir diciéndolos de a uno: el primero
    /// empieza a sonar mientras se arma la voz de los demás. Los muy cortos se quedan pegados al siguiente: "Dr. Smith",
    /// "Ok. Let's go." y "Hello everyone, thank you" no se parten.
    /// </summary>
    public static IReadOnlyList<string> Sentences(string text, int minChars = 40)
    {
        var result = new List<string>();
        var current = "";
        foreach (var piece in SentenceEnd().Split(text))
        {
            if (piece.Length == 0) continue;
            current = current.Length == 0 ? piece : current + " " + piece;
            if (current.Length < minChars) continue;
            result.Add(current);
            current = "";
        }
        if (current.Length == 0) return result;
        if (result.Count > 0 && current.Length < minChars) result[^1] += " " + current;
        else result.Add(current);
        return result;
    }

    [GeneratedRegex(@"(?<=[.!?…,;:])\s+")]
    private static partial Regex SentenceEnd();

    /// <summary>
    /// Una secuencia repetida seguida queda una sola vez: 1 o 2 palabras repetidas 3 veces o más ("no no no"; "very very
    /// good" se respeta), o 3 a 30 palabras repetidas 2 veces o más. Si después de la repetición el texto termina con el
    /// comienzo de otra vuelta (Whisper cortó por el tope de largo), ese resto también se va. Medido en la prueba de
    /// punta a punta: "Atmolysis, del griego, atmis, vapor, and lean." dicho dos veces y media = el triple de voz.
    /// </summary>
    public static string CollapseLoops(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var output = new List<string>(words.Length);
        var i = 0;
        while (i < words.Length)
        {
            var collapsed = false;
            for (var n = 1; n <= 30 && i + n * 2 <= words.Length; n++)
            {
                var repeats = 1;
                while (i + (repeats + 1) * n <= words.Length && Same(words, i, i + repeats * n, n)) repeats++;
                if (repeats < (n >= 3 ? 2 : 3)) continue;
                output.AddRange(words.AsSpan(i, n).ToArray());
                var next = i + repeats * n;
                var rest = words.Length - next;
                if (rest < n && Same(words, i, next, rest)) next = words.Length; // otra vuelta, cortada
                i = next;
                collapsed = true;
                break;
            }
            if (!collapsed) output.Add(words[i++]);
        }
        return string.Join(' ', output);
    }

    private static bool Same(string[] words, int a, int b, int n)
    {
        for (var k = 0; k < n; k++)
            if (!Key(words[a + k]).Equals(Key(words[b + k]), StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string Key(string word) => word.Trim(',', '.', '!', '?', ';', ':', '"', '…');

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex Annotation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // "Thank you" solo NO se descarta: "gracias" se dice en serio en una reunión, y el detector de voz ya evita que el
    // reconocedor reciba silencio. Los pedidos de suscripción y los créditos de subtítulos son lo que Whisper inventa
    // con muy poca voz (26/11: "¡Suscríbete!", "Suscribete y dale like"): nadie los dice en una reunión.
    [GeneratedRegex(@"^\W*(thanks for watching|thank you for watching|gracias por ver( el video)?|you|subtitles by .*|subt[ií]tulos (realizados |hechos |creados )?(por|de) .*|(please )?(like and )?subscribe\b.*|suscr[ií]bete\b.*)\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex Hallucination();
}
