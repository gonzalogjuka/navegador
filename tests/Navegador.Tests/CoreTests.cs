using Navegador.Core;

namespace Navegador.Tests;

public class BlockListTests
{
    private static readonly BlockList List = BlockList.Parse("""
        # comentario
        doubleclick.net
        0.0.0.0 tracker.example.com   # estilo hosts
        not a domain!
        localhost
        """);

    [Theory]
    [InlineData("doubleclick.net", true)]
    [InlineData("ad.g.doubleclick.net", true)]
    [InlineData("DoubleClick.NET.", true)]
    [InlineData("notdoubleclick.net", false)]
    [InlineData("tracker.example.com", true)]
    [InlineData("example.com", false)]
    [InlineData("localhost", false)]
    [InlineData("", false)]
    public void Matches_domain_and_subdomains_only(string host, bool blocked) =>
        Assert.Equal(blocked, List.IsBlocked(host));

    [Fact]
    public void Ignores_invalid_lines() =>
        Assert.Equal(["doubleclick.net", "tracker.example.com"], List.Domains.Order());
}

public class UrlInputTests
{
    private const string Search = "https://s/?q=";

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("example.com/path?x=1", "https://example.com/path?x=1")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("localhost:5173", "http://localhost:5173")]
    [InlineData("192.168.0.1", "https://192.168.0.1")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("  ", "about:blank")]
    [InlineData("como hacer pan", "https://s/?q=como%20hacer%20pan")]
    [InlineData("hola", "https://s/?q=hola")]
    [InlineData("node.js vs deno", "https://s/?q=node.js%20vs%20deno")]
    [InlineData("v1.2", "https://s/?q=v1.2")]
    public void Resolves_typed_text(string input, string expected) =>
        Assert.Equal(expected, UrlInput.Resolve(input, Search));
}

public class HibernationPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Settings S = new() { SuspendAfterSeconds = 60, DiscardAfterMinutes = 15, FocusLostSeconds = 10, RamBudgetMb = 1000, BudgetKeepRecentMinutes = 3 };

    private static TabSnapshot Tab(int id, double idleSeconds, TabState state = TabState.Live, bool active = false, bool audio = false, bool keep = false,
        bool meeting = false) =>
        new(id, active, state, Now.AddSeconds(-idleSeconds), audio, keep, meeting);

    [Fact]
    public void Meeting_tabs_are_never_lowered_slept_or_discarded()
    {
        // Reunión de fondo (estás en otra pestaña) y reunión activa con el foco en otra app: ninguna se toca,
        // ni por inactividad, ni por foco perdido, ni por el tope de RAM.
        TabSnapshot[] background = [Tab(1, 0, active: true), Tab(2, 3600, meeting: true)];
        Assert.DoesNotContain(Decide(background, focusLost: Now.AddSeconds(-60), mem: 5000, pages: 3000), x => x.TabId == 2);

        TabSnapshot[] active = [Tab(1, 0, active: true, meeting: true), Tab(2, 5)];
        Assert.DoesNotContain(Decide(active, focusLost: Now.AddSeconds(-60)), x => x.TabId == 1);
    }

    private static List<PolicyDecision> Decide(TabSnapshot[] tabs, DateTime? focusLost = null, double? mem = null, bool lowered = false,
        double pages = 0, BudgetDiscard? lastBudget = null) =>
        HibernationPolicy.Decide(tabs, Now, S, focusLost, mem is { } m ? new MemoryReading(m, pages) : null, lowered, lastBudget);

    [Fact]
    public void Background_tab_sleeps_after_idle_threshold_not_before()
    {
        var d = Decide([Tab(1, 0, active: true), Tab(2, 59), Tab(3, 60)]);
        Assert.Equal([new PolicyDecision(3, HibernateAction.Suspend, "idle")], d);
    }

    [Fact]
    public void Long_idle_tab_is_discarded_even_if_suspended()
    {
        var d = Decide([Tab(1, 0, active: true), Tab(2, 15 * 60, TabState.Suspended), Tab(3, 15 * 60, TabState.Discarded)]);
        Assert.Equal([new PolicyDecision(2, HibernateAction.Discard, "idle")], d);
    }

    [Fact]
    public void Active_and_audio_tabs_are_never_put_to_sleep()
    {
        var d = Decide([Tab(1, 3600, active: true), Tab(2, 3600, audio: true)], mem: 5000);
        Assert.Empty(d);
    }

    [Fact]
    public void Focus_loss_waits_threshold_then_sleeps_background_and_lowers_active_once()
    {
        TabSnapshot[] tabs = [Tab(1, 0, active: true), Tab(2, 5)];
        Assert.Empty(Decide(tabs, focusLost: Now.AddSeconds(-9)));

        var d = Decide(tabs, focusLost: Now.AddSeconds(-10));
        Assert.Contains(new PolicyDecision(1, HibernateAction.LowerMemory, "focus_lost"), d);
        Assert.Contains(new PolicyDecision(2, HibernateAction.Suspend, "focus_lost"), d);

        Assert.DoesNotContain(Decide(tabs, focusLost: Now.AddSeconds(-30), lowered: true), x => x.TabId == 1);
    }

    [Fact]
    public void Over_budget_discards_least_recently_used_background_tab()
    {
        var d = Decide([Tab(1, 0, active: true), Tab(2, 300), Tab(3, 600, TabState.Suspended), Tab(4, 400)], mem: 1001);
        Assert.Equal([new PolicyDecision(3, HibernateAction.Discard, "budget")], d.Where(x => x.Action == HibernateAction.Discard));
    }

    [Fact]
    public void Far_over_budget_discards_enough_tabs_at_once()
    {
        // 5 pestañas vivas con 1000 MB de páginas = 200 MB cada una; 600 MB sobre la meta (85 % de 1000) → hacen falta 3.
        TabSnapshot[] tabs = [Tab(1, 0, active: true), Tab(2, 840), Tab(3, 780), Tab(4, 720), Tab(5, 660)];
        var d = Decide(tabs, mem: 1450, pages: 1000);
        Assert.Equal([2, 3, 4], d.Where(x => x.Reason == "budget").Select(x => x.TabId).Order());
    }

    [Fact]
    public void Budget_never_discards_keep_alive_tabs()
    {
        // Formulario escrito: no se descarta aunque sobre memoria y sea la más vieja; la reciente sí, porque no queda otra.
        var d = Decide([Tab(1, 0, active: true), Tab(2, 60), Tab(3, 600, keep: true)], mem: 5000, pages: 3000);
        Assert.Equal([new PolicyDecision(2, HibernateAction.Discard, "budget")], d.Where(x => x.Action == HibernateAction.Discard));
    }

    [Fact]
    public void Recent_tabs_are_discarded_last()
    {
        // 4 vivas con 1200 MB de páginas = 300 MB cada una. La vieja (10 min) va primero; si hace falta más, las
        // recientes, empezando por la más vieja de ellas.
        TabSnapshot[] tabs = [Tab(1, 0, active: true), Tab(2, 30, TabState.Suspended), Tab(3, 90, TabState.Suspended), Tab(4, 600)];
        Assert.Equal([4], Decide(tabs, mem: 1100, pages: 1200).Where(x => x.Reason == "budget").Select(x => x.TabId));
        Assert.Equal([3, 4], Decide(tabs, mem: 1400, pages: 1200).Where(x => x.Reason == "budget").Select(x => x.TabId).Order());
    }

    [Fact]
    public void Burst_of_recent_tabs_discards_the_two_most_recent_last()
    {
        // Abriste 5 pestañas en el último minuto (500 MB cada una, 1450 MB sobre la meta): van las 3 primeras; las 2
        // últimas que miraste se quedan porque no hace falta más.
        TabSnapshot[] tabs = [Tab(1, 0, active: true), Tab(2, 50), Tab(3, 40), Tab(4, 30), Tab(5, 20), Tab(6, 10)];
        var d = Decide(tabs, mem: 2300, pages: 3000);
        Assert.Equal([2, 3, 4], d.Where(x => x.Reason == "budget").Select(x => x.TabId).Order());
    }

    [Fact]
    public void Room_is_made_before_loading_a_page_that_would_pass_the_budget()
    {
        // 800 MB con 3 vivas de 200 MB: una página más (200) pasaría la meta de 850 → se descarta la de fondo más vieja
        // y nunca la que va a cargar (skip), aunque sea la más vieja.
        TabSnapshot[] tabs = [Tab(1, 0, active: true), Tab(2, 600), Tab(3, 300), Tab(4, 900)];
        var mem = new MemoryReading(800, 600);
        var page = HibernationPolicy.EstimatePageMb(tabs, mem);
        Assert.Equal(150, page); // 600 MB ÷ 4 vivas
        Assert.Equal([2], HibernationPolicy.BudgetVictims(tabs, Now, S, mem, page, new HashSet<int> { 4 }));
        // Con lugar de sobra no se toca nada.
        Assert.Empty(HibernationPolicy.BudgetVictims(tabs, Now, S, new MemoryReading(500, 300), page, new HashSet<int> { 4 }));
    }

    [Fact]
    public void Keep_alive_tab_is_only_suspended_when_idle_for_long()
    {
        var d = Decide([Tab(1, 0, active: true), Tab(2, 20 * 60, keep: true)]);
        Assert.Equal([new PolicyDecision(2, HibernateAction.Suspend, "idle")], d);
    }

    [Fact]
    public void Budget_waits_cooldown_unless_memory_keeps_rising()
    {
        TabSnapshot[] tabs = [Tab(1, 0, active: true), Tab(2, 600, TabState.Suspended)];
        Assert.Empty(Decide(tabs, mem: 2000, lastBudget: new(Now.AddSeconds(-14), 2000)));
        Assert.Single(Decide(tabs, mem: 2000, lastBudget: new(Now.AddSeconds(-15), 2000)));
        Assert.Single(Decide(tabs, mem: 2100, lastBudget: new(Now.AddSeconds(-5), 2000))); // se siguen cargando páginas
    }

    [Fact]
    public void Budget_victim_skips_tabs_already_discarded_by_idle_rule()
    {
        var d = Decide([Tab(1, 0, active: true), Tab(2, 20 * 60), Tab(3, 300)], mem: 2000);
        Assert.Contains(new PolicyDecision(2, HibernateAction.Discard, "idle"), d);
        Assert.Contains(new PolicyDecision(3, HibernateAction.Discard, "budget"), d);
    }

    [Fact]
    public void Under_budget_does_nothing_extra() =>
        Assert.Empty(Decide([Tab(1, 0, active: true), Tab(2, 30)], mem: 1000));
}

public class PageCostsTests
{
    private static readonly DateTime T0 = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Cost_is_the_peak_rise_within_the_window_and_only_known_after_it()
    {
        var c = new PageCosts();
        c.Start("www.google.com", 300, T0);
        c.Observe(850, T0.AddSeconds(5));
        c.Observe(700, T0.AddSeconds(10));
        Assert.Null(c.CostOf("www.google.com")); // todavía cargando
        c.Observe(1200, T0.AddSeconds(16)); // ya fuera de la ventana: cuenta igual como pico de la carga
        Assert.Equal(900, c.CostOf("WWW.GOOGLE.COM"));
        Assert.Null(c.CostOf("example.com"));
    }

    [Fact]
    public void A_lighter_reload_halves_a_heavy_cost_at_most()
    {
        var c = new PageCosts();
        c.Start("maps", 200, T0);
        c.Observe(800, T0.AddSeconds(15));
        c.Start("maps", 500, T0.AddSeconds(60));
        c.Observe(550, T0.AddSeconds(75));
        Assert.Equal(300, c.CostOf("maps"));
    }
}

public class PersistenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("navegador-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Settings_corrupt_or_invalid_values_fall_back_to_defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{"RamBudgetMb": 5, "SuspendAfterSeconds": 30, "SearchUrl": ""}""");
        var s = Settings.LoadOrCreate(path);
        Assert.Equal(1024, s.RamBudgetMb);
        Assert.Equal(30, s.SuspendAfterSeconds);
        Assert.StartsWith("https://", s.SearchUrl);

        File.WriteAllText(path, "{ roto");
        Assert.Equal(60, Settings.LoadOrCreate(path).SuspendAfterSeconds);
    }

    [Fact]
    public void Allow_toggle_is_case_insensitive_and_persists()
    {
        var path = Path.Combine(_dir, "settings.json");
        var s = Settings.LoadOrCreate(path);
        Assert.True(s.ToggleAllowed("Example.com"));
        s.Save(path);
        var reloaded = Settings.LoadOrCreate(path);
        Assert.True(reloaded.IsAllowed("example.COM"));
        Assert.False(reloaded.ToggleAllowed("EXAMPLE.com"));
        Assert.False(reloaded.IsAllowed("example.com"));
    }

    [Fact]
    public void Session_roundtrip_clamps_active_index_and_rejects_corrupt()
    {
        var path = Path.Combine(_dir, "session.json");
        SessionStore.Save(path, new SavedSession([new("https://a", "A"), new("https://b", "B")], 7));
        var s = SessionStore.Load(path)!;
        Assert.Equal(["https://a", "https://b"], s.Tabs.Select(t => t.Url));
        Assert.Equal(1, s.ActiveIndex);

        File.WriteAllText(path, "{");
        Assert.Null(SessionStore.Load(path));
    }
}
