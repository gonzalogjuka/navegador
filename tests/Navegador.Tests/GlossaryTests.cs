using Navegador.Core.Voz;

namespace Navegador.Tests;

public class GlossaryTests
{
    private const int Today = 739_945;

    private static Glossary Learned(params string[] phrases)
    {
        var g = new Glossary();
        foreach (var phrase in phrases) g.Learn(phrase, Today);
        return g;
    }

    [Fact]
    public void User_terms_always_go_in_the_prompt_and_comments_do_not()
    {
        var g = new Glossary();
        g.SetUserTerms(["# mis términos", "Control Cloud", "", "  Kubernetes  ", "control cloud"]);
        Assert.Equal("Glossary: Control Cloud, Kubernetes.", g.Prompt);
        Assert.Equal("", new Glossary().Prompt);
    }

    [Fact]
    public void A_name_is_learned_when_it_shows_up_in_two_phrases()
    {
        var once = Learned("Last week we deployed the frontend in Vercel.");
        Assert.Equal("", once.Prompt);
        var twice = Learned("Last week we deployed the frontend in Vercel.", "I think Vercel is down.");
        Assert.Equal("Glossary: Vercel.", twice.Prompt);
        // Dicho dos veces en la misma frase cuenta una sola: una frase mal oída no alcanza para aprender un término.
        Assert.Equal("", Learned("We use Vercel because Vercel is simple.").Prompt);
    }

    [Fact]
    public void Words_that_only_open_a_sentence_are_not_names()
    {
        var g = Learned("Perfect, then we stay like this. Yesterday we talked about it.",
            "Perfect, the budget is fine. Yesterday it was not.", "Tuesday is fine. I think so.", "On Tuesday I can.");
        Assert.Equal("", g.Prompt);
    }

    [Fact]
    public void Names_of_several_words_acronyms_and_inner_capitals_are_learned()
    {
        var g = Learned("Control Cloud lets you see all your infrastructure.", "We built Control Cloud last year.",
            "AWS is expensive.", "The bill from AWS, again.", "It is on GitHub now.", "GitHub's servers are down.");
        Assert.Equal(["AWS", "Control Cloud", "GitHub"], g.LearnedTerms.Order());
    }

    [Fact]
    public void A_comma_separates_two_names()
    {
        var g = Learned("We use Supabase, Vercel and Terraform.", "It runs on Supabase, Vercel and Terraform.");
        Assert.Equal(["Supabase", "Terraform", "Vercel"], g.LearnedTerms.Order());
    }

    [Fact]
    public void The_most_used_terms_come_first_and_the_prompt_has_a_limit()
    {
        var g = new Glossary();
        g.SetUserTerms(["Orni"]);
        for (var i = 0; i < 3; i++) g.Learn("We talked to Vercel.", Today);
        for (var i = 0; i < 2; i++) g.Learn("We talked to Supabase.", Today);
        Assert.Equal("Glossary: Orni, Vercel, Supabase.", g.Prompt);

        for (var n = 0; n < 60; n++)
            for (var i = 0; i < 2; i++) g.Learn($"We talked to Empresa{n:00}Larguisima.", Today);
        Assert.InRange(g.Prompt.Length, 100, Glossary.MaxPromptChars + 1);
        Assert.StartsWith("Glossary: Orni, Vercel, ", g.Prompt); // el glosario escrito a mano y lo más usado no se caen
    }

    [Fact]
    public void What_was_learned_survives_a_restart_and_old_terms_are_forgotten()
    {
        var path = Path.Combine(Path.GetTempPath(), "astro-glosario-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var g = Learned("We talked to Vercel.", "We talked to Vercel again.");
            Assert.True(g.Dirty);
            g.SaveLearned(path);
            Assert.False(g.Dirty);

            var tomorrow = new Glossary();
            tomorrow.LoadLearned(path, Today + 1);
            Assert.Equal("Glossary: Vercel.", tomorrow.Prompt);

            var nextYear = new Glossary();
            nextYear.LoadLearned(path, Today + Glossary.ForgetAfterDays + 1);
            Assert.Equal("", nextYear.Prompt);

            File.WriteAllText(path, "{ esto no es json");
            var broken = new Glossary();
            broken.LoadLearned(path, Today);
            Assert.Equal("", broken.Prompt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_user_file_is_created_with_instructions_and_read_back()
    {
        var path = Path.Combine(Path.GetTempPath(), "astro-glosario-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var g = new Glossary();
            g.LoadUserFile(path);
            Assert.Empty(g.UserTerms); // el archivo nuevo sólo trae instrucciones y ejemplos comentados
            File.AppendAllText(path, "Control Cloud\nOCI\n");
            g.LoadUserFile(path);
            Assert.Equal(["Control Cloud", "OCI"], g.UserTerms);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
