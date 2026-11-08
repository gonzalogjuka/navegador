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
    private static readonly Settings S = new() { SuspendAfterSeconds = 60, DiscardAfterMinutes = 15, FocusLostSeconds = 10, RamBudgetMb = 1000 };

    private static TabSnapshot Tab(int id, double idleSeconds, TabState state = TabState.Live, bool active = false, bool audio = false) =>
        new(id, active, state, Now.AddSeconds(-idleSeconds), audio);

    private static List<PolicyDecision> Decide(TabSnapshot[] tabs, DateTime? focusLost = null, double? mem = null, bool lowered = false) =>
        HibernationPolicy.Decide(tabs, Now, S, focusLost, mem, lowered);

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
        var d = Decide([Tab(1, 0, active: true), Tab(2, 30), Tab(3, 120, TabState.Suspended), Tab(4, 10)], mem: 1001);
        Assert.Equal([new PolicyDecision(3, HibernateAction.Discard, "budget")], d);
    }

    [Fact]
    public void Budget_victim_skips_tabs_already_discarded_by_idle_rule()
    {
        var d = Decide([Tab(1, 0, active: true), Tab(2, 20 * 60), Tab(3, 30)], mem: 2000);
        Assert.Contains(new PolicyDecision(2, HibernateAction.Discard, "idle"), d);
        Assert.Contains(new PolicyDecision(3, HibernateAction.Discard, "budget"), d);
    }

    [Fact]
    public void Under_budget_does_nothing_extra() =>
        Assert.Empty(Decide([Tab(1, 0, active: true), Tab(2, 30)], mem: 1000));
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
