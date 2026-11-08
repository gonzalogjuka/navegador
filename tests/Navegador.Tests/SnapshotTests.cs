using Navegador.Core;

namespace Navegador.Tests;

public class SnapshotTests
{
    [Fact]
    public void Only_generated_names_are_accepted()
    {
        Assert.True(SnapshotFiles.IsValidName(SnapshotFiles.NewName()));
        Assert.False(SnapshotFiles.IsValidName(null));
        Assert.False(SnapshotFiles.IsValidName(@"..\..\settings.json"));
        Assert.False(SnapshotFiles.IsValidName(@"C:\Windows\win.ini"));
        Assert.False(SnapshotFiles.IsValidName("0123456789abcdef0123456789abcdef.png"));
        Assert.False(SnapshotFiles.IsValidName("0123456789ABCDEF0123456789ABCDEF.jpg"));
    }

    [Fact]
    public void Orphans_are_files_no_tab_references()
    {
        var a = SnapshotFiles.NewName();
        var b = SnapshotFiles.NewName();
        var c = SnapshotFiles.NewName();
        Assert.Equal(new[] { b, c }.Order(), SnapshotFiles.Orphans([a, b, c], [a, null]).Order());
    }

    [Fact]
    public void Session_keeps_scroll_and_snapshot_and_reads_old_format()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("navegador-snap").FullName, "session.json");
        var name = SnapshotFiles.NewName();
        SessionStore.Save(path, new SavedSession([new("https://a/", "A", "[0,2000]", name)], 0));
        var t = SessionStore.Load(path)!.Tabs.Single();
        Assert.Equal("[0,2000]", t.Scroll);
        Assert.Equal(name, t.Snapshot);

        File.WriteAllText(path, """{"Tabs":[{"Url":"https://a/","Title":"A"}],"ActiveIndex":0}""");
        var old = SessionStore.Load(path)!.Tabs.Single();
        Assert.Null(old.Scroll);
        Assert.Null(old.Snapshot);
    }
}
