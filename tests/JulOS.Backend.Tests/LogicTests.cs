using System.Text;
using JulOS.Backend.Logic;
using JulOS.Backend.Services;

namespace JulOS.Backend.Tests;

public sealed class LogicTests
{
    [Fact]
    public void Base32_matches_the_rfc_example_and_round_trips()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Base32.Encode(secret));
        Assert.Equal(secret, Base32.Decode("gezd gnbv gy3t qojq gezd gnbv gy3t qojq"));
        for (var n = 0; n < 40; n++)
        {
            var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(n);
            Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
        }
        Assert.Throws<FormatException>(() => Base32.Decode("ab1"));
    }

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void Hotp_matches_the_rfc_6238_test_vectors(long seconds, string expected) =>
        Assert.Equal(expected, Totp.Hotp(Encoding.ASCII.GetBytes("12345678901234567890"), seconds / 30, digits: 8));

    [Fact]
    public void Totp_check_accepts_neighbours_rejects_far_and_used_steps()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);
        var step = now.ToUnixTimeSeconds() / 30;
        string At(long s) => Totp.Hotp(Base32.Decode(secret), s);
        Assert.Equal(step, Totp.Check(secret, At(step), 0, now));
        Assert.Equal(step - 1, Totp.Check(secret, At(step - 1), 0, now));
        Assert.Equal(step + 1, Totp.Check(secret, At(step + 1), 0, now));
        Assert.Null(Totp.Check(secret, At(step + 2), 0, now));
        Assert.Null(Totp.Check(secret, At(step - 2), 0, now));
        Assert.Null(Totp.Check(secret, At(step), step, now));          // verbraucht
        Assert.Equal(step + 1, Totp.Check(secret, At(step + 1), step, now));
        Assert.Null(Totp.Check(secret, "12345", 0, now));
        Assert.Null(Totp.Check(secret, "12a456", 0, now));
        Assert.StartsWith("otpauth://totp/JulOS:jul?secret=" + secret, Totp.Uri("jul", secret));
    }

    [Fact]
    public void Recovery_codes_are_unique_normalised_and_hashed()
    {
        var (plain, hashes) = RecoveryCodes.Generate();
        Assert.Equal(10, plain.Length);
        Assert.Equal(10, plain.Distinct().Count());
        Assert.All(plain, c => Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$", c));
        Assert.Equal(hashes[0], RecoveryCodes.Hash(plain[0].ToLowerInvariant().Replace("-", " ")));
        Assert.True(RecoveryCodes.LooksLikeRecoveryCode(plain[3]));
        Assert.False(RecoveryCodes.LooksLikeRecoveryCode("123456"));
    }

    [Fact]
    public void Limiter_locks_with_growing_wait_and_forgets()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var limiter = new LoginLimiter(time);
        for (var i = 0; i < 4; i++) { limiter.Fail("user:a"); Assert.Equal(TimeSpan.Zero, limiter.Wait("user:a")); }
        limiter.Fail("user:a");
        Assert.Equal(TimeSpan.FromSeconds(30), limiter.Wait("user:a"));
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(TimeSpan.Zero, limiter.Wait("user:a"));
        limiter.Fail("user:a");
        Assert.Equal(TimeSpan.FromSeconds(60), limiter.Wait("user:a"));
        for (var i = 0; i < 30; i++) limiter.Fail("user:a");
        Assert.Equal(TimeSpan.FromMinutes(15), limiter.Wait("user:a"));         // Obergrenze
        Assert.Equal(TimeSpan.Zero, limiter.Wait("user:b"));
        limiter.Succeed("user:a");
        Assert.Equal(TimeSpan.Zero, limiter.Wait("user:a"));
        for (var i = 0; i < 9; i++) limiter.Fail("ip:x");
        Assert.Equal(TimeSpan.Zero, limiter.Wait("ip:x"));                      // Adressen sind grosszuegiger
        limiter.Fail("ip:x");
        Assert.True(limiter.Wait("ip:x") > TimeSpan.Zero);
        time.Advance(TimeSpan.FromHours(2));
        limiter.Fail("user:c");
        limiter.Sweep();
        Assert.Equal(TimeSpan.Zero, limiter.Wait("ip:x"));
    }

    [Fact]
    public void Password_and_user_name_rules()
    {
        Assert.Equal("jul", PasswordPolicy.NormalizeUserName("  Jul "));
        Assert.Null(PasswordPolicy.NormalizeUserName("a b"));
        Assert.Null(PasswordPolicy.NormalizeUserName("../x"));
        Assert.Null(PasswordPolicy.NormalizeUserName(new string('a', 33)));
        Assert.NotEqual("", PasswordPolicy.Problem("kurz", "jul"));
        Assert.NotEqual("", PasswordPolicy.Problem("aaaaaaaaaaaaaaaa", "jul"));
        Assert.NotEqual("", PasswordPolicy.Problem("mein-JUL-passwort", "jul"));
        Assert.Equal("", PasswordPolicy.Problem("korrektes pferd batterie", "jul"));
    }

    [Fact]
    public async Task Password_hash_is_argon2id_salted_and_verifiable()
    {
        var hasher = new PasswordHasher();
        var a = await hasher.HashAsync("korrektes pferd batterie");
        var b = await hasher.HashAsync("korrektes pferd batterie");
        Assert.StartsWith("argon2id$m=65536,t=3,p=1$", a);
        Assert.NotEqual(a, b);
        Assert.Equal((true, false), await hasher.VerifyAsync("korrektes pferd batterie", a));
        Assert.Equal((false, false), await hasher.VerifyAsync("korrektes pferd batteriE", a));
        Assert.Equal((false, false), await hasher.VerifyAsync("x", "kaputt"));
        // alter, schwaecherer Hash (8 MiB, 2 Durchgaenge): stimmt das Passwort, wird er als "neu schreiben" gemeldet
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        using var weakArgon = new Konscious.Security.Cryptography.Argon2id(Encoding.UTF8.GetBytes("korrektes pferd batterie")) { Salt = salt, MemorySize = 8192, Iterations = 2, DegreeOfParallelism = 1 };
        var weak = $"argon2id$m=8192,t=2,p=1${Convert.ToBase64String(salt)}${Convert.ToBase64String(weakArgon.GetBytes(32))}";
        Assert.Equal((true, true), await hasher.VerifyAsync("korrektes pferd batterie", weak));
    }

    [Fact]
    public void Setup_code_is_random_grouped_single_use_and_tolerant_in_typing()
    {
        var code = new SetupCode();
        Assert.Matches("^[A-Z2-7]{4}-[A-Z2-7]{4}-[A-Z2-7]{4}$", code.Value);
        Assert.NotEqual(code.Value, new SetupCode().Value);
        Assert.True(code.Matches(code.Value.ToLowerInvariant().Replace("-", " ")));
        Assert.False(code.Matches("AAAA-AAAA-AAAA"));
        Assert.False(code.Matches(null));
        code.MarkUsed();
        Assert.False(code.Matches(code.Value));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void Client_addresses_are_keyed_by_ipv4_address_or_ipv6_prefix(string address, string key) =>
        Assert.Equal(key, ClientAddress.Key(System.Net.IPAddress.Parse(address)));

    [Fact]
    public void Client_address_key_without_address_is_a_placeholder() => Assert.Equal("?", ClientAddress.Key(null));

    [Fact]
    public void Login_limiter_caps_the_checks_running_at_the_same_time_per_key()
    {
        var limiter = new LoginLimiter();
        Assert.True(limiter.TryBegin("ip:a", 2));
        Assert.True(limiter.TryBegin("ip:a", 2));
        Assert.False(limiter.TryBegin("ip:a", 2));       // die dritte wird abgewiesen und zaehlt nicht mit
        Assert.True(limiter.TryBegin("ip:b", 2));        // andere Schluessel sind frei
        limiter.End("ip:a");
        Assert.True(limiter.TryBegin("ip:a", 2));        // ein Platz wurde frei
        limiter.End("ip:a"); limiter.End("ip:a");
        Assert.True(limiter.TryBegin("ip:a", 1));        // alles frei: wieder von vorn
    }

    [Fact]
    public void A_vnc_connection_has_port_5900_no_domain_no_drive_and_the_default_layout()
    {
        var v = ConnectionRules.Validate("Alt", "vnc", "ziel", null, "", driveShare: true, driveWrite: true, domain: "DOM", layout: "fr").Value!;
        Assert.Equal(("vnc", 5900), (v.Kind, v.Port));
        Assert.False(v.DriveShare);
        Assert.False(v.DriveWrite);
        Assert.Equal("", v.Domain);
        Assert.Equal(KeyboardLayouts.Default, v.Layout);
        Assert.Equal(5901, ConnectionRules.Validate("Alt", "vnc", "ziel", 5901, "").Value!.Port);
        Assert.Equal(3389, ConnectionRules.Validate("PC", "desktop", "ziel", null, "").Value!.Port);
        Assert.NotNull(ConnectionRules.Validate("Alt", "telnet", "ziel", null, "").Error);
    }

    [Fact]
    public void Drive_switches_default_to_off_belong_to_desktops_and_writing_needs_the_share()
    {
        var plain = ConnectionRules.Validate("PC", "desktop", "ziel", null, "u").Value!;
        Assert.False(plain.DriveShare);
        Assert.False(plain.DriveWrite);
        var share = ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", driveShare: true).Value!;
        Assert.True(share.DriveShare);
        Assert.False(share.DriveWrite);
        var both = ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", driveShare: true, driveWrite: true).Value!;
        Assert.True(both is { DriveShare: true, DriveWrite: true });
        Assert.False(ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", driveWrite: true).Value!.DriveWrite);
        Assert.False(ConnectionRules.Validate("PC", "terminal", "ziel", null, "u", driveShare: true).Value!.DriveShare);

        // without an answer the earlier value stays (a rename must not turn a switch off or on) - but only while it still applies
        Assert.True(ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", null, null, true, true).Value is { DriveShare: true, DriveWrite: true });
        Assert.False(ConnectionRules.Validate("PC", "terminal", "ziel", null, "u", null, null, true, true).Value!.DriveShare);
        Assert.False(ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", false, null, true, true).Value!.DriveWrite);
    }

    [Fact]
    public void Domain_notes_and_layout_have_defaults_belong_to_desktops_and_keep_the_earlier_value_when_not_given()
    {
        var plain = ConnectionRules.Validate("PC", "desktop", "ziel", null, "u").Value!;
        Assert.Equal(("", "", "de"), (plain.Domain, plain.Notes, plain.Layout));
        var set = ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", domain: " CORP ", notes: " a\r\nb ", layout: "en-us").Value!;
        Assert.Equal(("CORP", "a\nb", "en-us"), (set.Domain, set.Notes, set.Layout));
        var kept = ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", currentDomain: "CORP", currentNotes: "n", currentLayout: "fr").Value!;
        Assert.Equal(("CORP", "n", "fr"), (kept.Domain, kept.Notes, kept.Layout));
        var term = ConnectionRules.Validate("PC", "terminal", "ziel", null, "u", domain: "CORP", notes: "n", layout: "fr").Value!;
        Assert.Equal(("", "n", "de"), (term.Domain, term.Notes, term.Layout));
        Assert.Null(ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", layout: "xx").Value);
        Assert.Null(ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", domain: "a b").Value);
        Assert.Null(ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", notes: new string('x', ConnectionRules.MaxNotes + 1)).Value);
        Assert.NotNull(ConnectionRules.Validate("PC", "desktop", "ziel", null, "u", notes: new string('x', ConnectionRules.MaxNotes)).Value);
    }

    [Theory]
    [InlineData("de", 0x407u, "de")]
    [InlineData("de-at", 0xC07u, "de")]
    [InlineData("de-ch", 0x807u, "qwertz")]
    [InlineData("en-us", 0x409u, "qwerty")]
    [InlineData("en-gb", 0x809u, "qwerty")]
    [InlineData("fr", 0x40Cu, "azerty")]
    [InlineData("es", 0x40Au, "qwerty")]
    [InlineData("it", 0x410u, "qwerty")]
    [InlineData("unbekannt", 0x407u, "de")]      // a code from a later version is German, never an error at connect time
    public void Every_layout_maps_to_a_windows_id_and_a_key_style(string code, uint windows, string client)
    {
        Assert.Equal(windows, KeyboardLayouts.WindowsId(code));
        Assert.Equal(client, KeyboardLayouts.ClientKey(code));
        Assert.Equal(code != "unbekannt", KeyboardLayouts.IsKnown(code));
    }

    [Fact]
    public void Network_tool_targets_are_the_saved_hosts_inside_the_allow_list()
    {
        string[] saved = ["b.example", "A.example", "a.example", "192.0.2.5", "fremd.example"];
        Assert.Equal(["192.0.2.5", "A.example", "b.example", "fremd.example"], NetToolRules.Targets(saved, new HostPolicy((string?)null)));
        var policy = new HostPolicy("a.example, 192.0.2.5");
        Assert.Equal(["192.0.2.5", "A.example"], NetToolRules.Targets(saved, policy));
        Assert.Equal("A.example", NetToolRules.Resolve(" a.EXAMPLE ", saved, policy));         // any spelling, the saved one comes back
        Assert.Null(NetToolRules.Resolve("b.example", saved, policy));                          // saved but not allowed
        Assert.Null(NetToolRules.Resolve("169.254.169.254", saved, new HostPolicy((string?)null)));      // not saved
        Assert.Null(NetToolRules.Resolve("", saved, policy));
        Assert.Null(NetToolRules.Resolve(null, saved, policy));
    }

    [Fact]
    public void Network_tool_ports_and_ping_count_are_limited()
    {
        Assert.Equal([22, 3389], NetToolRules.Ports([22, 3389, 22]).Ports!);
        Assert.Null(NetToolRules.Ports(null).Ports);
        Assert.Null(NetToolRules.Ports([]).Ports);
        Assert.Null(NetToolRules.Ports([0]).Ports);
        Assert.Null(NetToolRules.Ports([65536]).Ports);
        Assert.NotNull(NetToolRules.Ports(Enumerable.Range(1, NetToolRules.MaxPorts)).Ports);
        Assert.Null(NetToolRules.Ports(Enumerable.Range(1, NetToolRules.MaxPorts + 1)).Ports);
        Assert.Equal(4, NetToolRules.PingCount(null));
        Assert.Equal(1, NetToolRules.PingCount(0));
        Assert.Equal(NetToolRules.MaxPingCount, NetToolRules.PingCount(1000));
    }
}
