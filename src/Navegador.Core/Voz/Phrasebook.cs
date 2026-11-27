using System.Globalization;
using System.Text;

namespace Navegador.Core.Voz;

/// <summary>
/// Libro de frases del intérprete: modismos y giros en español con la expresión que usaría un nativo en inglés
/// ("no me cierra = doesn't add up"). Cuando lo que dijiste contiene uno, va de pista al modelo de lenguaje, que solo
/// tiende a traducirlo literal (medido el 26/11 con Qwen 1.5B: sin pista "doesn't close the deal", con pista "doesn't
/// add up"). El archivo es de la persona: una expresión por línea, <c>español = inglés</c>; varias formas del español se
/// separan con <c>|</c>; las líneas que empiezan con # no cuentan.
/// </summary>
public sealed class Phrasebook
{
    /// <summary>Pistas por frase: más de dos confunden a un modelo chico y alargan cada frase.</summary>
    public const int MaxHints = 2;

    private readonly List<(string Key, string Es, string En)> _entries = [];

    public int Count => _entries.Count;

    public static Phrasebook Parse(IEnumerable<string> lines)
    {
        var book = new Phrasebook();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var eq = line.LastIndexOf('=');
            if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
            var en = line[(eq + 1)..].Trim();
            if (en.Length == 0) continue;
            foreach (var form in line[..eq].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var key = Normalize(form);
                if (key.Length >= 3 && !book._entries.Any(e => e.Key == key)) book._entries.Add((key, form, en));
            }
        }
        // Las formas más largas primero: "se nos fue de las manos" gana sobre una más corta que también coincida.
        book._entries.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
        return book;
    }

    /// <summary>Las expresiones del libro que aparecen en la frase (palabras enteras, sin mirar mayúsculas ni acentos).</summary>
    public IReadOnlyList<(string Es, string En)> Hints(string spanish)
    {
        var hints = new List<(string Es, string En)>();
        if (_entries.Count == 0) return hints;
        var text = " " + Normalize(spanish) + " ";
        foreach (var (key, es, en) in _entries)
        {
            if (!text.Contains(" " + key + " ", StringComparison.Ordinal)) continue;
            if (hints.Any(h => h.En == en)) continue; // otra forma de la misma expresión
            hints.Add((es, en));
            if (hints.Count == MaxHints) break;
        }
        return hints;
    }

    /// <summary>Minúsculas, sin acentos ni puntuación, con un solo espacio entre palabras.</summary>
    public static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        var space = true;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                result.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                result.Append(' ');
                space = true;
            }
        }
        return result.ToString().Trim();
    }

    /// <summary>Lee el libro; si no existe, lo deja con las expresiones de arranque.</summary>
    public static Phrasebook Load(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, Starter, new UTF8Encoding(false));
            return Parse(File.ReadAllLines(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Parse(Starter.Split('\n'));
        }
    }

    /// <summary>Expresiones de arranque: giros rioplatenses y de reunión que un modelo chico traduce literal.</summary>
    public const string Starter =
        """
        # Libro de frases del intérprete de Astro: cómo querés que diga en inglés tus expresiones.
        # Una por línea:  español = inglés   (varias formas del español se separan con | ).
        # Agregá las tuyas, corregí las que no te gusten; se usan desde la próxima reunión.
        # En voz-reuniones\ quedan las expresiones de cada reunión: las que te sirvan, copialas acá.

        estamos al horno | estoy al horno | están al horno | estamos en el horno = we're in trouble
        ponerse las pilas | ponernos las pilas | ponete las pilas | pongámonos las pilas | ponerme las pilas = step it up
        no me cierra | no nos cierra | no me cierran = doesn't add up
        se nos fue de las manos | se me fue de las manos | se fue de las manos | se nos va de las manos = got out of hand
        salir a producción | salimos a producción | sale a producción | pasar a producción | salga a producción = go live
        quedamos así = let's leave it at that
        me viene bien | nos viene bien = works for me
        no doy abasto | no damos abasto = can't keep up
        estoy a full | estamos a full | estoy tapado | estamos tapados = swamped
        al toque = right away
        tirar ideas | tiremos ideas | tirando ideas = brainstorm
        bajar a tierra | bajarlo a tierra | bajamos a tierra = make it concrete
        patear para adelante | pateamos para adelante | patearlo para adelante = push it back
        a ojo = as a rough estimate
        ir al grano | vamos al grano | voy al grano = get to the point
        dar una mano | dame una mano | darte una mano | darles una mano | me das una mano = give a hand
        los mantengo al tanto | te mantengo al tanto | les voy contando | te voy contando = I'll keep you posted
        estoy al tanto | estamos al tanto = I'm aware of it
        seguimos en contacto | quedamos en contacto = let's keep in touch
        cualquier cosa me avisan | cualquier cosa avisame | cualquier cosa avisen | cualquier cosa me avisás = let me know if anything comes up
        a grandes rasgos = roughly speaking
        en resumidas cuentas = long story short
        por las dudas = just in case
        está bárbaro | me parece bárbaro | bárbaro = sounds great
        no pasa nada = no worries
        dar de baja | damos de baja | dimos de baja = shut down
        dar de alta | damos de alta | dimos de alta = set up
        levantar el servidor | levantar el servicio | levantamos el servicio | levantar un servidor = spin up
        se cayó | se cayeron | está caído | está caída = went down
        se colgó | quedó colgado | se cuelga = froze
        hacer un seguimiento | hacemos un seguimiento | hacerle seguimiento = follow up
        cerrar el tema | cerramos el tema | cerremos el tema = wrap this up
        tema aparte = on a separate note
        de entrada = right off the bat
        con lo justo = cutting it close
        vamos viendo | lo vamos viendo = let's play it by ear
        sobre la marcha = as we go
        a esta altura = at this point
        la estamos remando | hay que remarla | la venimos remando = it's an uphill battle
        zafamos | zafé = we dodged a bullet
        un quilombo | quilombo = a mess
        laburo | laburando | laburar = work
        guita = money
        comparto pantalla | compartir pantalla | les comparto pantalla = share my screen
        se entrecorta | se te entrecorta | te escucho entrecortado = you're breaking up
        estás muteado | estás silenciado | tenés el micrófono apagado = you're on mute
        te robo un minuto | les robo un minuto = can I grab you for a minute
        """;
}
