using JulOS.Integrations;

namespace JulOS.Backend.Tests;

public sealed class SessionRegistryTests
{
    private static readonly Guid Me = Guid.NewGuid();

    [Fact]
    public void A_new_session_in_the_same_slot_replaces_only_that_one()
    {
        var registry = new SessionRegistry(8);
        var replaced = new List<string>();
        var a = registry.TryOpen(Me, "pc:s1", () => replaced.Add("a"));
        var b = registry.TryOpen(Me, "pc:s2", () => replaced.Add("b"));
        var a2 = registry.TryOpen(Me, "pc:s1", () => replaced.Add("a2"));

        Assert.NotNull(a); Assert.NotNull(b); Assert.NotNull(a2);
        Assert.Equal(["a"], replaced);                       // nur die Sitzung s1 wurde abgeloest, s2 nicht
        Assert.True(a!.Replaced);
        Assert.False(b!.Replaced);
        Assert.Equal(2, registry.CountOf(Me));               // die Ablosung zaehlt nicht doppelt
    }

    [Fact]
    public void The_limit_applies_per_user_and_a_replacement_still_works_at_the_limit()
    {
        var registry = new SessionRegistry(2);
        var other = Guid.NewGuid();
        Assert.NotNull(registry.TryOpen(Me, "a", () => { }));
        Assert.NotNull(registry.TryOpen(Me, "b", () => { }));
        Assert.Null(registry.TryOpen(Me, "c", () => { }));                    // dritte Sitzung: Obergrenze
        Assert.False(registry.CanOpen(Me, "c"));
        Assert.True(registry.CanOpen(Me, "a"));                               // ein Neuladen der vorhandenen geht
        Assert.NotNull(registry.TryOpen(Me, "a", () => { }));
        Assert.NotNull(registry.TryOpen(other, "a", () => { }));              // ein anderer Benutzer ist nicht betroffen
        Assert.NotNull(registry.TryOpen(other, "b", () => { }));
    }

    [Fact]
    public void Disposing_frees_the_place_but_an_old_lease_never_frees_its_successor()
    {
        var registry = new SessionRegistry(1);
        var first = registry.TryOpen(Me, "a", () => { })!;
        var second = registry.TryOpen(Me, "a", () => { })!;
        first.Dispose();                                                      // die abgeloeste endet spaet: die neue bleibt eingetragen
        Assert.Equal(1, registry.CountOf(Me));
        Assert.Null(registry.TryOpen(Me, "b", () => { }));
        second.Dispose();
        Assert.Equal(0, registry.CountOf(Me));
        Assert.NotNull(registry.TryOpen(Me, "b", () => { }));
    }

    [Fact]
    public void Sessions_without_a_slot_each_count_and_never_replace_each_other()
    {
        var registry = new SessionRegistry(2);
        var a = registry.TryOpen(Me, null, () => { });
        var b = registry.TryOpen(Me, null, () => { });
        Assert.False(a!.Replaced);
        Assert.Null(registry.TryOpen(Me, null, () => { }));
        b!.Dispose();
        Assert.NotNull(registry.TryOpen(Me, null, () => { }));
    }

    [Fact]
    public void The_slot_of_a_ticket_includes_the_browser_session_id_only_when_it_is_clean()
    {
        var conn = Guid.NewGuid();
        Assert.Equal($"{Me}:{conn}:abcd1234", SessionRegistry.SlotOf(Me, conn, "abcd1234"));
        Assert.Equal($"{Me}:{conn}", SessionRegistry.SlotOf(Me, conn, null));
        Assert.Equal($"{Me}:{conn}", SessionRegistry.SlotOf(Me, conn, "kurz"));
        Assert.Equal($"{Me}:{conn}", SessionRegistry.SlotOf(Me, conn, "mit Leerzeichen;oder:Doppelpunkt"));
        Assert.Equal($"{Me}:{conn}", SessionRegistry.SlotOf(Me, conn, new string('a', 41)));
    }
}
