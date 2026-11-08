using Navegador.Core;

namespace Navegador.Tests;

public class BrowserDataTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Visits_dedupe_by_url_and_keep_latest_title()
    {
        var d = new BrowserData();
        d.AddVisit("https://a.com/", "A", T0);
        d.AddVisit("https://a.com", "", T0.AddMinutes(1));   // misma página sin la barra final
        d.UpdateTitle("https://A.com", "A nuevo");
        var e = Assert.Single(d.History);
        Assert.Equal(2, e.Visits);
        Assert.Equal("A nuevo", e.Title);
        Assert.Equal(T0.AddMinutes(1), e.LastVisitUtc);
    }

    [Fact]
    public void Bookmark_matches_regardless_of_trailing_slash()
    {
        var d = new BrowserData();
        d.ToggleBookmark("https://a.com", "A");
        Assert.True(d.IsBookmarked("https://a.com/"));
        Assert.False(d.ToggleBookmark("https://a.com/", "A"));
        Assert.Empty(d.Bookmarks);
    }

    [Fact]
    public void Only_web_urls_are_recorded_or_bookmarked()
    {
        var d = new BrowserData();
        d.AddVisit("about:blank", "x", T0);
        d.AddVisit("file:///C:/secret.txt", "x", T0);
        Assert.False(d.ToggleBookmark("javascript:alert(1)", "x"));
        Assert.Empty(d.History);
        Assert.Empty(d.Bookmarks);
    }

    [Fact]
    public void History_is_capped_dropping_oldest()
    {
        var d = new BrowserData();
        for (var i = 0; i <= BrowserData.MaxHistory; i++) d.AddVisit($"https://s{i}.com/", "", T0.AddSeconds(i));
        Assert.Equal(BrowserData.MaxHistory, d.History.Count);
        Assert.DoesNotContain(d.History, h => h.Url == "https://s0.com/");
        Assert.Equal("https://s2000.com/", d.Recent(1).Single().Url);
    }

    [Fact]
    public void Bookmark_toggle_adds_then_removes_and_survives_save()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("navegador-data").FullName, "data.json");
        var d = new BrowserData();
        Assert.True(d.ToggleBookmark("https://a.com/", ""));
        d.Save(path);
        var loaded = BrowserData.Load(path);
        Assert.Equal("https://a.com/", Assert.Single(loaded.Bookmarks).Title);
        Assert.False(loaded.ToggleBookmark("https://a.com/", "A"));
        Assert.Empty(loaded.Bookmarks);
        File.WriteAllText(path, "{roto");
        Assert.Empty(BrowserData.Load(path).Bookmarks);
    }
}

public class HttpsUpgradeTests
{
    [Theory]
    [InlineData("http://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("http://www.infobae.com", "https://www.infobae.com/")]
    [InlineData("https://example.com", null)]
    [InlineData("http://localhost/x", null)]
    [InlineData("http://192.168.0.1/", null)]
    [InlineData("http://[::1]/", null)]
    [InlineData("http://router/", null)]
    [InlineData("http://nas.local/", null)]
    [InlineData("http://example.com:8080/", null)]
    [InlineData("ftp://example.com/", null)]
    public void Upgrades_only_public_default_port_http(string url, string? expected) =>
        Assert.Equal(expected, HttpsUpgrade.Upgrade(url, new HashSet<string>()));

    [Fact]
    public void Exempt_host_and_its_subdomains_stay_on_http()
    {
        var exempt = new HashSet<string> { "neverssl.com" };
        Assert.Null(HttpsUpgrade.Upgrade("http://neverssl.com/", exempt));
        Assert.Null(HttpsUpgrade.Upgrade("http://abc.neverssl.com/", exempt));
        Assert.NotNull(HttpsUpgrade.Upgrade("http://notneverssl.com/", exempt));
    }
}

public class InternalPagesTests
{
    [Fact]
    public void Site_controlled_text_is_escaped_and_unsafe_links_are_dropped()
    {
        var d = new BrowserData();
        d.AddVisit("https://evil.com/?q=\"><script>alert(1)</script>", "<img src=x onerror=alert(1)>", DateTime.UtcNow);
        d.ToggleBookmark("https://ok.com/", "\"><b>x</b>");
        d.Bookmarks.Add(new Bookmark("javascript:alert(1)", "malo")); // p. ej. data.json editado a mano

        var html = InternalPages.BuildNewTab(d, "https://www.google.com/search?q=", isPrivate: false)
                   + InternalPages.BuildHistory(d);

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<b>x</b>", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
    }

    [Fact]
    public void Search_form_targets_engine_with_its_query_field()
    {
        var html = InternalPages.BuildNewTab(new BrowserData(), "https://duckduckgo.com/?kp=-2&q=", isPrivate: false);
        Assert.Contains("action=\"https://duckduckgo.com/\"", html);
        Assert.Contains("<input type=\"hidden\" name=\"kp\" value=\"-2\">", html);
        Assert.Contains("<input name=\"q\"", html);
    }

    [Fact]
    public void Private_new_tab_does_not_show_history()
    {
        var d = new BrowserData();
        d.AddVisit("https://secreto.com/", "Secreto", DateTime.UtcNow);
        Assert.DoesNotContain("secreto.com", InternalPages.BuildNewTab(d, "https://s/?q=", isPrivate: true));
        Assert.Contains("secreto.com", InternalPages.BuildNewTab(d, "https://s/?q=", isPrivate: false));
    }
}
