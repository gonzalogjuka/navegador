using Navegador.Core.Voz;

namespace Navegador.Tests;

public class PhrasebookTests
{
    private static readonly Phrasebook Book = Phrasebook.Parse(
    [
        "# comentario",
        "no me cierra | no nos cierra = doesn't add up",
        "se nos fue de las manos | se fue de las manos = got out of hand",
        "está bárbaro | bárbaro = sounds great",
        "al toque = right away",
        "sin igual",
        " = nada",
    ]);

    [Fact]
    public void An_idiom_in_the_phrase_becomes_a_hint_whatever_the_case_accents_or_punctuation()
    {
        Assert.Equal([("no me cierra", "doesn't add up")], Book.Hints("El número que me pasaron NO me cierra."));
        Assert.Equal([("está bárbaro", "sounds great")], Book.Hints("Sí, lo vi ayer, esta barbaro!"));
        Assert.Empty(Book.Hints("Hoy cierra el trimestre."));
        Assert.Empty(new Phrasebook().Hints("no me cierra"));
    }

    [Fact]
    public void Only_whole_words_match()
    {
        Assert.Empty(Book.Hints("Fue al toquecito."));          // "al toque" no es "al toquecito"
        Assert.Single(Book.Hints("Lo arreglo al toque, tranquilo."));
    }

    [Fact]
    public void The_longest_form_wins_and_one_expression_gives_one_hint()
    {
        // "se nos fue de las manos" contiene "se fue de las manos"? No: son formas distintas de la misma expresión.
        var hints = Book.Hints("El mes pasado se nos fue de las manos y está bárbaro que lo veamos.");
        Assert.Equal([("se nos fue de las manos", "got out of hand"), ("está bárbaro", "sounds great")], hints);
    }

    [Fact]
    public void A_phrase_never_gets_more_than_two_hints()
    {
        var hints = Book.Hints("No me cierra, se fue de las manos, está bárbaro y lo veo al toque.");
        Assert.Equal(Phrasebook.MaxHints, hints.Count);
    }

    [Fact]
    public void The_starter_book_parses_and_knows_the_usual_idioms()
    {
        var starter = Phrasebook.Parse(Phrasebook.Starter.Split('\n'));
        Assert.True(starter.Count > 80);
        Assert.Equal("we're in trouble", Assert.Single(starter.Hints("La verdad, estamos al horno con los tiempos.")).En);
        Assert.Equal("step it up", Assert.Single(starter.Hints("Hay que ponerse las pilas con las pruebas.")).En);
        Assert.Equal("go live", Assert.Single(starter.Hints("¿Qué nos falta para salir a producción?")).En);
    }

    [Fact]
    public void The_file_is_created_with_the_starter_and_the_users_lines_are_read()
    {
        var path = Path.Combine(Path.GetTempPath(), "astro-frases-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var first = Phrasebook.Load(path);
            Assert.True(File.Exists(path));
            File.AppendAllText(path, "\nme la juego = I'd bet on it\n");
            var second = Phrasebook.Load(path);
            Assert.Equal(first.Count + 1, second.Count);
            Assert.Equal("I'd bet on it", Assert.Single(second.Hints("Yo me la juego.")).En);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class MeetingNotesTests
{
    private static MeetingNotes Heard(params string[] lines)
    {
        var notes = new MeetingNotes();
        foreach (var line in lines) notes.Heard(line);
        return notes;
    }

    private static readonly string[] Meeting =
    [
        "Before we dive in, can you walk us through where things stand?",
        "We are on the same page about the testing, but the deadline is set in stone.",
        "When do you think the billing module will be ready? We need a ballpark figure.",
        "Sounds good. Let us touch base next week and take it from there.",
    ];

    [Fact]
    public void Too_little_heard_is_not_worth_asking_the_model()
    {
        Assert.Null(Heard("We are on the same page about the testing.", "Yeah.", "Okay.").AskPrompt());
        var prompt = Heard(Meeting).AskPrompt();
        Assert.NotNull(prompt);
        Assert.Contains("- We are on the same page about the testing, but the deadline is set in stone.\n", prompt);
    }

    [Fact]
    public void Only_expressions_that_were_really_said_are_kept()
    {
        var notes = Heard(Meeting);
        var parsed = notes.ParseAnswer(
            "1. on the same page = de acuerdo\n" +
            "- \"set in stone\" = inamovible\n" +
            "ballpark figure = cifra aproximada\n" +
            "hit the ground running = arrancar con todo\n" +          // no se dijo: el modelo la inventó
            "deadline = fecha límite\n" +                             // una sola palabra
            "When do you think the billing module will be ready = cuándo estará listo\n" + // una oración entera
            "On the same page = de acuerdo\n" +                       // repetida
            "touch base\n");                                           // sin significado
        Assert.Equal([("on the same page", "de acuerdo"), ("set in stone", "inamovible"), ("ballpark figure", "cifra aproximada")], parsed);
    }

    [Fact]
    public void The_block_for_the_file_has_both_lists_and_empties_the_notes()
    {
        var notes = Heard(Meeting);
        notes.Said("El número no me cierra.", "The number doesn't add up.");
        notes.Said("El número no me cierra.", "The number doesn't add up."); // dicha dos veces: una sola línea
        Assert.True(notes.WorthSaving);
        var block = notes.Render(new DateTime(2026, 11, 26, 21, 5, 0), [("set in stone", "inamovible")]);
        Assert.Equal(
            "## 21:05\n\n" +
            "Expresiones que usaron los demás (inglés = qué quiere decir):\n- set in stone = inamovible\n\n" +
            "Tus expresiones y cómo se dijeron (español = inglés):\n- El número no me cierra. = The number doesn't add up.\n\n", block);
        Assert.True(notes.Empty);
        Assert.Equal("", notes.Render(DateTime.Now, []));
    }

    [Fact]
    public void Only_the_last_sentences_are_remembered()
    {
        var notes = new MeetingNotes();
        for (var i = 0; i < MeetingNotes.MaxHeard + 10; i++) notes.Heard($"This is sentence number {i} of the meeting.");
        Assert.Equal(MeetingNotes.MaxHeard, notes.HeardCount);
        Assert.DoesNotContain("sentence number 0 ", notes.AskPrompt());
    }
}
