using JulOS.Backend.Logic;
using JulOS.Integrations.Ai;

namespace JulOS.Backend.Tests;

/// <summary>Reine Regeln des KI-Zugangs: App-Schluessel, erlaubte Modelle, Budgets, Sperre nach Fehlversuchen, gekuerzte Fehlertexte, Auswertung der Codex-Ausgabe.</summary>
public sealed class AiRulesTests
{
    [Fact]
    public void Keys_are_random_hashed_and_only_found_by_their_own_hash()
    {
        var a = AiRules.NewKey(); var b = AiRules.NewKey();
        Assert.StartsWith(AiRules.KeyPrefix, a);
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 50);                                  // 256 Bit Zufall, kodiert
        Assert.Equal(AiRules.HashKey(a), AiRules.HashKey(a));
        Assert.Equal(4, AiRules.KeyHint(a).Length);

        var found = AiRules.Match(AiRules.HashKey(b), [(AiRules.HashKey(a), "a"), (AiRules.HashKey(b), "b")]);
        Assert.Equal("b", found);
        Assert.Null(AiRules.Match(AiRules.HashKey("jos_ai_other"), [(AiRules.HashKey(a), "a")]));

        Assert.Equal("abc", AiRules.BearerKey("Bearer abc"));
        Assert.Equal("abc", AiRules.BearerKey("bearer   abc "));
        Assert.Null(AiRules.BearerKey("Basic abc")); Assert.Null(AiRules.BearerKey("")); Assert.Null(AiRules.BearerKey(null)); Assert.Null(AiRules.BearerKey("Bearer " + new string('x', 201)));
    }

    [Fact]
    public void Model_permissions_name_a_provider_or_one_model()
    {
        Assert.True(AiRules.Allows(["openai"], "openai/gpt-4.1"));
        Assert.True(AiRules.Allows(["openai/gpt-4.1"], "openai/gpt-4.1"));
        Assert.False(AiRules.Allows(["openai/gpt-4.1"], "openai/gpt-4.1-mini"));
        Assert.False(AiRules.Allows(["openai"], "anthropic/claude"));
        Assert.False(AiRules.Allows(["openai"], "openaix/gpt"));        // kein Teilwort
        Assert.False(AiRules.Allows(["openai"], "gpt-4.1")); Assert.False(AiRules.Allows(["openai"], "openai/")); Assert.False(AiRules.Allows([], "openai/x"));

        Assert.Equal(["openai", "codex/gpt-5"], AiRules.ValidateAllowed([" openai", "codex/gpt-5", "openai"], false).Allowed!);
        Assert.NotNull(AiRules.ValidateAllowed([], false).Error); Assert.NotNull(AiRules.ValidateAllowed(["evil"], false).Error); Assert.NotNull(AiRules.ValidateAllowed(["fake"], false).Error);
        Assert.Null(AiRules.ValidateAllowed(["fake"], true).Error);
    }

    [Fact]
    public void Budgets_warn_at_80_percent_and_stop_at_100()
    {
        Assert.Null(new AiBudget(0, 100, 0, 1000).Warning); Assert.Null(new AiBudget(79, 100, 0, null).Warning);
        Assert.Equal("day", new AiBudget(80, 100, 0, null).Warning);
        Assert.Equal("month", new AiBudget(0, null, 800, 1000).Warning);
        Assert.Null(new AiBudget(80, 100, 0, null).Exceeded);
        Assert.Equal("day", new AiBudget(100, 100, 0, 1000).Exceeded);
        Assert.Equal("month", new AiBudget(5, 100, 1000, 1000).Exceeded);
        Assert.Null(new AiBudget(100, 100, 0, null).Warning);                    // bei 100 % ist es kein Hinweis mehr, sondern ein Stopp
        Assert.Null(new AiBudget(1_000_000, null, 1_000_000, null).Exceeded);    // ohne Grenze nie

        Assert.Null(AiRules.ValidateLimits(100, 1000, 30)); Assert.Null(AiRules.ValidateLimits(null, null, 1));
        Assert.NotNull(AiRules.ValidateLimits(0, null, 30)); Assert.NotNull(AiRules.ValidateLimits(2000, 1000, 30)); Assert.NotNull(AiRules.ValidateLimits(null, null, 0)); Assert.NotNull(AiRules.ValidateLimits(null, null, 601));
        Assert.NotNull(AiRules.ValidateName(" ")); Assert.NotNull(AiRules.ValidateName(new string('x', 61))); Assert.Null(AiRules.ValidateName("Notizen"));
    }

    [Fact]
    public void Failed_keys_block_an_address_before_a_key_is_checked_and_the_block_ends_with_the_window()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-11-23T10:00:00Z"));
        var limiter = new AiAuthLimiter(time, max: 3);
        for (var i = 0; i < 3; i++) { Assert.False(limiter.Blocked("1.2.3.4")); limiter.Fail("1.2.3.4"); }
        Assert.True(limiter.Blocked("1.2.3.4"));                 // gesperrt, bevor der vierte Schluessel angesehen wird
        Assert.False(limiter.Blocked("5.6.7.8"));                // eine andere Adresse ist frei
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(limiter.Blocked("1.2.3.4"));
    }

    [Fact]
    public void Concurrency_is_limited_per_app()
    {
        var gate = new AiConcurrency(2); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.True(gate.TryEnter(a)); Assert.True(gate.TryEnter(a)); Assert.False(gate.TryEnter(a));
        Assert.True(gate.TryEnter(b));
        gate.Leave(a); Assert.True(gate.TryEnter(a));
    }

    [Fact]
    public void Provider_errors_are_cut_and_keys_are_removed_before_an_app_sees_them()
    {
        var shown = AiText.Safe("Incorrect API key provided: sk-proj-abcdef1234567890. Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature123456 jos_ai_secretsecret " + new string('x', 400));
        Assert.DoesNotContain("sk-proj", shown); Assert.DoesNotContain("eyJ", shown); Assert.DoesNotContain("jos_ai_secret", shown);
        Assert.True(shown.Length <= AiText.MaxErrorLength + 1);
        Assert.Equal("", AiText.Safe(null)); Assert.Equal("kurz und gut", AiText.Safe("  kurz \n und   gut "));
        Assert.Equal(3, AiText.EstimateTokens("abcdefghi")); Assert.Equal(0, AiText.EstimateTokens(""));
    }

    [Fact]
    public void Device_login_output_gives_only_an_https_address_of_openai_and_a_code()
    {
        Assert.Equal("https://auth.openai.com/codex/device", CodexProtocol.LoginAddress("\u001b[1mOpen\u001b[0m \u001b[36mhttps://auth.openai.com/codex/device\u001b[0m")!.AbsoluteUri);
        Assert.Null(CodexProtocol.LoginAddress("http://auth.openai.com/x"));                       // nur https
        Assert.Null(CodexProtocol.LoginAddress("https://evil.example/openai.com"));                // nur OpenAIs eigener Host
        Assert.Null(CodexProtocol.LoginAddress("https://notopenai.com/x"));
        Assert.Null(CodexProtocol.LoginAddress("https://user:pw@auth.openai.com/x"));
        Assert.Equal("ABCD-12345", CodexProtocol.LoginCode("  \u001b[1mABCD-12345\u001b[0m "));
        Assert.Null(CodexProtocol.LoginCode("Enter this one-time code")); Assert.Null(CodexProtocol.LoginCode("abcd-12345"));
    }

    [Fact]
    public void Codex_models_usage_and_conversations_are_read_and_flattened()
    {
        var models = CodexProtocol.ParseModels(ApiKit.J("""{"data":[{"model":"gpt-x","displayName":"X","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"high"}],"defaultReasoningEffort":"low"},{"id":"hid","hidden":true},{"id":"y"}]}"""));
        Assert.Equal(["gpt-x", "y"], models.Select(m => m.Id).ToArray());
        Assert.Equal(["low", "high"], models[0].Efforts); Assert.Equal("low", models[0].DefaultEffort); Assert.Equal("y", models[1].Name);

        var usage = CodexProtocol.ParseUsage(ApiKit.J("""{"rateLimits":{"planType":"plus","primary":{"usedPercent":140,"windowDurationMins":300,"resetsAt":1900000000},"secondary":{"usedPercent":3}}}"""))!;
        Assert.Equal("plus", usage.Plan); Assert.Equal(100, usage.Windows[0].UsedPercent); Assert.Equal(300, usage.Windows[0].Minutes); Assert.Equal(3, usage.Windows[1].UsedPercent);
        Assert.Null(CodexProtocol.ParseUsage(ApiKit.J("{}")));
        Assert.NotNull(CodexProtocol.PlanLimitIn("You've hit your usage limit. Try again at ...")); Assert.Null(CodexProtocol.PlanLimitIn("other"));

        var (instructions, prompt) = CodexBridge.Flatten([new AiMessage("user", "Hallo")]);
        Assert.Equal("Hallo", prompt); Assert.False(string.IsNullOrWhiteSpace(instructions));
        var (i2, p2) = CodexBridge.Flatten([new AiMessage("system", "Sei kurz."), new AiMessage("user", "A"), new AiMessage("assistant", "B"), new AiMessage("user", "C")]);
        Assert.Equal("Sei kurz.", i2); Assert.Equal("User: A\n\nAssistant: B\n\nUser: C", p2);
    }
}
