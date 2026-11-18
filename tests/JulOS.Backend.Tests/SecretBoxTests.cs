using JulOS.Backend.Logic;

namespace JulOS.Backend.Tests;

public sealed class SecretBoxTests
{
    private static string Key(byte seed) => Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + seed)).ToArray());

    [Fact]
    public void A_password_round_trips_and_every_encryption_differs()
    {
        var box = new SecretBox(Key(1));
        var id = Guid.NewGuid();
        var a = box.Encrypt("pässwörd 🔑", id);
        var b = box.Encrypt("pässwörd 🔑", id);
        Assert.NotEqual(a, b);                                  // frische Nonce
        Assert.Equal("pässwörd 🔑", box.Decrypt(a, id));
    }

    [Fact]
    public void Wrong_key_other_connection_or_tampering_gives_nothing()
    {
        var box = new SecretBox(Key(1));
        var id = Guid.NewGuid();
        var sealedValue = box.Encrypt("geheim", id);
        Assert.Null(new SecretBox(Key(2)).Decrypt(sealedValue, id));
        Assert.Null(box.Decrypt(sealedValue, Guid.NewGuid()));  // an eine andere Verbindung verschoben
        sealedValue[^1] ^= 1;
        Assert.Null(box.Decrypt(sealedValue, id));
        Assert.Null(box.Decrypt([1, 2, 3], id));
    }

    [Fact]
    public void Without_a_key_it_is_off_and_a_bad_key_stops_the_start()
    {
        Assert.False(new SecretBox(null).Enabled);
        Assert.False(new SecretBox(" ").Enabled);
        Assert.Throws<InvalidOperationException>(() => new SecretBox("kein base64!"));
        Assert.Throws<InvalidOperationException>(() => new SecretBox(Convert.ToBase64String(new byte[16])));
        Assert.Throws<InvalidOperationException>(() => new SecretBox(null).Encrypt("x", Guid.NewGuid()));
    }
}
