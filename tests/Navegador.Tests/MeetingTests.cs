using Navegador.Core;

namespace Navegador.Tests;

public class MeetingSitesTests
{
    [Theory]
    [InlineData("meet.google.com", true)]
    [InlineData("teams.microsoft.com", true)]
    [InlineData("teams.live.com", true)]
    [InlineData("app.zoom.us", true)]
    [InlineData("us02web.zoom.us", true)]
    [InlineData("whereby.com", true)]
    [InlineData("MEET.GOOGLE.COM.", true)]
    [InlineData("google.com", false)]
    [InlineData("mail.google.com", false)]
    [InlineData("notzoom.us", false)]
    [InlineData("zoom.us.evil.com", false)]
    [InlineData(null, false)]
    public void Recognizes_meeting_sites_only(string? host, bool meeting) =>
        Assert.Equal(meeting, MeetingSites.IsMeeting(host));
}

public class MeetingMixTests
{
    [Theory]
    [InlineData(MeetingVoice.Auto)]
    [InlineData(MeetingVoice.Original)]
    [InlineData(MeetingVoice.Translated)]
    [InlineData(MeetingVoice.Both)]
    public void Without_translator_the_meeting_hears_the_original_voice(MeetingVoice voice)
    {
        var mix = MeetingMix.For(voice, translatorConnected: false);
        Assert.Equal(1, mix.Original);
        Assert.Equal(0, mix.Translated);
        Assert.False(mix.Capture); // nadie escucha: no se manda el micrófono
    }

    [Fact]
    public void Missing_translator_is_flagged_only_when_the_translation_was_asked_for()
    {
        Assert.False(MeetingMix.For(MeetingVoice.Auto, false).TranslatorMissing);
        Assert.False(MeetingMix.For(MeetingVoice.Original, false).TranslatorMissing);
        Assert.True(MeetingMix.For(MeetingVoice.Translated, false).TranslatorMissing);
        Assert.True(MeetingMix.For(MeetingVoice.Both, false).TranslatorMissing);
    }

    [Fact]
    public void With_translator_auto_sends_only_the_translation()
    {
        Assert.Equal(new MeetingMix(0, 1, true, false), MeetingMix.For(MeetingVoice.Auto, true));
        Assert.Equal(new MeetingMix(0, 1, true, false), MeetingMix.For(MeetingVoice.Translated, true));
    }

    [Fact]
    public void Both_keeps_the_original_low_under_the_translation()
    {
        var mix = MeetingMix.For(MeetingVoice.Both, true);
        Assert.True(mix.Original is > 0 and < 1);
        Assert.Equal(1, mix.Translated);
        Assert.True(mix.Capture);
    }

    [Fact]
    public void Original_never_sends_the_microphone_to_the_translator()
    {
        Assert.Equal(new MeetingMix(1, 0, false, false), MeetingMix.For(MeetingVoice.Original, true));
    }
}
