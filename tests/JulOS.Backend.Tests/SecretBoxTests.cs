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

    [Fact]
    public void The_key_file_is_created_once_and_then_kept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "julos-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = SecretBox.ResolveKey(null, dir);
            Assert.NotNull(first);
            Assert.True(new SecretBox(first).Enabled);                                    // gueltiger Schluessel (32 Byte)
            Assert.Equal(first, SecretBox.ResolveKey("", dir));                           // zweiter Start: dieselbe Datei, derselbe Schluessel
            Assert.Equal(first, File.ReadAllText(Path.Combine(dir, "secret.key")).Trim());
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "secret.key")));
            var box = new SecretBox(first); var id = Guid.NewGuid();
            Assert.Equal("geheim", new SecretBox(SecretBox.ResolveKey(null, dir)).Decrypt(box.Encrypt("geheim", id), id));   // ein Neustart liest alte Eintraege
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_environment_key_wins_and_without_any_source_or_a_read_only_folder_storing_stays_off()
    {
        var dir = Path.Combine(Path.GetTempPath(), "julos-key-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Key(3), SecretBox.ResolveKey(Key(3), dir));
        Assert.False(Directory.Exists(dir));                                              // mit gesetzter Variable wird keine Datei angelegt
        Assert.Null(SecretBox.ResolveKey(null, null));
        Assert.Null(SecretBox.ResolveKey(null, " "));
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
            Assert.Null(SecretBox.ResolveKey(null, "/proc/julos-nicht-beschreibbar"));    // nicht beschreibbar: Speichern aus, kein Absturz
    }
}
