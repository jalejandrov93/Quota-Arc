using QuotaArc.Model;
using QuotaArc.Providers;

namespace QuotaArc.Tests;

public class CodexBarUsageTests
{
    private static readonly IReadOnlyList<CodexBarItem> Items = CodexBarUsage.Parse(CodexBarFixture.Usage);

    private static ProviderSnapshot Snap(string key) => CodexBarUsage.Snapshot(key, Items);

    private static DateTime Local(string iso) => DateTimeOffset.Parse(iso).LocalDateTime;

    [Fact]
    public void DecodesEveryItemAndIgnoresUnknownFields() =>
        Assert.Equal(7, Items.Count);

    [Fact]
    public void ClaudeMapsItsWindowsAndTheScopedExtra()
    {
        var snap = Snap("claude");
        Assert.IsType<ProviderStatus.Ok>(snap.Status);
        Assert.Equal(new[] { "primary", "secondary", "claude-weekly-scoped-fable" }, snap.Windows.Select(w => w.Id));
        Assert.Equal(new[] { "Session", "Weekly", "Fable only" }, snap.Windows.Select(w => w.Label));
        Assert.Equal(0.52, snap.Windows[0].UsedFraction ?? -1, 4);
        Assert.Equal(0.48, snap.Windows[1].UsedFraction ?? -1, 4);
        Assert.Equal(0.06, snap.Windows[2].UsedFraction ?? -1, 4);
        Assert.Equal(Local("2026-09-15T19:50:00Z"), snap.Windows[0].ResetsAt);
        Assert.Equal("primary", snap.Headline?.Id);
        Assert.Equal(Fidelity.Official, snap.Fidelity);
        Assert.Equal(ProviderGlyph.Claude, snap.Glyph);
        Assert.Equal("Claude", snap.DisplayName);
        Assert.True(snap.IsRemote);
    }

    [Fact]
    public void FractionalPercentsStayFractional() =>
        Assert.Equal(0.755092, Snap("antigravity").Windows[0].UsedFraction ?? -1, 6);

    [Fact]
    public void SameLengthWindowsKeepDistinctLabels()
    {
        var labels = Snap("antigravity").Windows.Select(w => w.Label).ToList();
        Assert.Equal(
            new[] { "Weekly", "Weekly 2", "Gemini 5-hour", "Gemini weekly", "Claude/GPT 5-hour", "Claude/GPT weekly" },
            labels);
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Fact]
    public void OpenCodeGoMapsAllThreeWindowsInOrder()
    {
        var snap = Snap("opencodego");
        Assert.Equal(new[] { "primary", "secondary", "tertiary" }, snap.Windows.Select(w => w.Id));
        Assert.Equal(new[] { "Session", "Weekly", "Monthly" }, snap.Windows.Select(w => w.Label));
        Assert.Equal(ProviderGlyph.Opencode, snap.Glyph);
        Assert.Equal("OpenCode Go", snap.DisplayName);
    }

    [Fact]
    public void AQuotaWithoutAWindowLengthReadsAsMonthly()
    {
        var snap = Snap("copilot");
        var window = Assert.Single(snap.Windows);
        Assert.Equal("Monthly", window.Label);
        Assert.Equal(0.997, window.UsedFraction ?? -1, 4);
        Assert.Equal(ProviderGlyph.Third, snap.Glyph);
        Assert.Equal("Copilot", snap.DisplayName);
    }

    [Theory]
    [InlineData(300.0, false, "Session")]
    [InlineData(1440.0, false, "Daily")]
    [InlineData(10080.0, false, "Weekly")]
    [InlineData(43200.0, false, "Monthly")]
    [InlineData(120.0, false, "2h")]
    [InlineData(4320.0, false, "3d")]
    [InlineData(90.0, false, "90m")]
    [InlineData(null, true, "Monthly")]
    [InlineData(null, false, "Quota")]
    public void LabelsFollowTheWindowLength(double? minutes, bool onlyPrimary, string expected) =>
        Assert.Equal(expected, CodexBarUsage.WindowLabel(minutes, onlyPrimary));

    [Fact]
    public void UsedFractionIsClampedToTheRing()
    {
        const string json = """
        [ { "provider": "glm", "usage": {
            "primary": { "usedPercent": 130, "windowMinutes": 300 },
            "secondary": { "usedPercent": -5, "windowMinutes": 10080 } } } ]
        """;
        var snap = CodexBarUsage.Snapshot("glm", CodexBarUsage.Parse(json));
        Assert.Equal(1.0, snap.Windows[0].UsedFraction);
        Assert.Equal(0.0, snap.Windows[1].UsedFraction);
        Assert.Null(snap.Windows[0].ResetsAt);
    }

    [Fact]
    public void AnUnauthorizedAccountNeedsAuth()
    {
        var snap = Snap("codex:user@example.com");
        Assert.IsType<ProviderStatus.NeedsAuth>(snap.Status);
        Assert.Empty(snap.Windows);
        Assert.Equal("Codex · user@example.com", snap.DisplayName);
        Assert.Equal(ProviderGlyph.Openai, snap.Glyph);
        Assert.True(snap.IsRemote);
    }

    [Fact]
    public void ARuntimeFailureIsAnError()
    {
        var snap = Snap("opencode");
        Assert.Equal(
            new ProviderStatus.Error("Error: selected source requires web support and is only supported on macOS."),
            snap.Status);
        Assert.Empty(snap.Windows);
    }

    [Theory]
    [InlineData("GET https://example.com failed: 401")]
    [InlineData("Unauthorized")]
    [InlineData("Please sign in again")]
    [InlineData("Kilo CLI session not found. Run `kilo auth login`")]
    [InlineData("Amp access token not configured.")]
    [InlineData("Azure OpenAI API key not configured.")]
    public void AuthFailuresNeedAuth(string message) =>
        Assert.IsType<ProviderStatus.NeedsAuth>(CodexBarUsage.StatusForError(message));

    [Theory]
    [InlineData("No available fetch strategy for grok.")]
    [InlineData("Claude usage probe timed out.")]
    public void OtherFailuresAreErrors(string message) =>
        Assert.Equal(new ProviderStatus.Error(message), CodexBarUsage.StatusForError(message));

    [Fact]
    public void ErrorsKeepOnlyAShortFirstLine()
    {
        Assert.Equal(new ProviderStatus.Error("first line"), CodexBarUsage.StatusForError("first line\nsecond line"));
        var longOne = (ProviderStatus.Error)CodexBarUsage.StatusForError(new string('x', 400));
        Assert.InRange(longOne.Why.Length, 1, 140);
    }

    [Fact]
    public void AProviderMissingFromTheResponseIsUnsupported()
    {
        var snap = Snap("cursor");
        Assert.Equal(new ProviderStatus.Unsupported("Not enabled in CodexBar"), snap.Status);
        Assert.Empty(snap.Windows);
        Assert.Equal(ProviderGlyph.Cursor, snap.Glyph);
    }

    [Fact]
    public void AccountsOfTheSameProviderAreReadSeparately()
    {
        var working = Snap("codex:user2@example.com");
        Assert.IsType<ProviderStatus.Ok>(working.Status);
        Assert.Equal(0.125, working.Windows[0].UsedFraction ?? -1, 4);
        Assert.IsType<ProviderStatus.NeedsAuth>(Snap("codex:user@example.com").Status);
        Assert.IsType<ProviderStatus.Unsupported>(Snap("codex:someone@example.com").Status);
    }

    [Fact]
    public void AKeyWithoutAnAccountFallsBackToTheProvidersFirstEntry() =>
        Assert.IsType<ProviderStatus.NeedsAuth>(Snap("codex").Status);

    [Theory]
    [InlineData("claude", "Claude")]
    [InlineData("codex", "Openai")]
    [InlineData("cursor", "Cursor")]
    [InlineData("antigravity", "Antigravity")]
    [InlineData("gemini", "Antigravity")]
    [InlineData("opencode", "Opencode")]
    [InlineData("opencodego", "Opencode")]
    [InlineData("glm", "Glm")]
    [InlineData("zai", "Glm")]
    [InlineData("grok", "Grok")]
    [InlineData("copilot", "Third")]
    [InlineData("amp", "Third")]
    public void GlyphFollowsTheProvider(string provider, string expected) =>
        Assert.Equal(Enum.Parse<ProviderGlyph>(expected), CodexBarUsage.GlyphFor(provider));

    [Theory]
    [InlineData("claude", null, "Claude")]
    [InlineData("codex", "a@example.com", "Codex · a@example.com")]
    [InlineData("antigravity", null, "Antigravity")]
    [InlineData("opencodego", null, "OpenCode Go")]
    [InlineData("copilot", null, "Copilot")]
    [InlineData("kilo", null, "Kilo")]
    public void DisplayNameFollowsTheProvider(string provider, string? account, string expected) =>
        Assert.Equal(expected, CodexBarUsage.DisplayName(provider, account));

    [Fact]
    public void KeysSplitOnTheFirstColon()
    {
        Assert.Equal(("codex", (string?)"user@example.com"), CodexBarUsage.SplitKey("codex:user@example.com"));
        Assert.Equal(("claude", (string?)null), CodexBarUsage.SplitKey("claude"));
        Assert.Equal("codex:user@example.com", CodexBarUsage.KeyFor("codex", "user@example.com"));
        Assert.Equal("claude", CodexBarUsage.KeyFor("claude", null));
    }

    [Fact]
    public void ProviderKeysExpandEveryAccountInEnabledOrder()
    {
        string[] enabled = ["claude", "codex", "antigravity", "opencodego", "copilot"];
        Assert.Equal(
            new[] { "claude", "codex:user@example.com", "codex:user2@example.com", "antigravity", "opencodego", "copilot" },
            CodexBarUsage.ProviderKeys(enabled, Items));
        Assert.Equal(enabled, CodexBarUsage.ProviderKeys(enabled, null));
    }

    [Fact]
    public void AccountComesFromTheReportedIdentity()
    {
        var claude = CodexBarUsage.AccountFrom(CodexBarUsage.Find(Items, "claude"));
        Assert.NotNull(claude);
        Assert.Equal("user@example.com", claude.Label);
        Assert.Equal("Claude Max 5x", claude.Plan);
        Assert.Equal("CodexBar (WSL)", claude.Source);
        Assert.Null(claude.ManageUrl);

        Assert.Null(CodexBarUsage.AccountFrom(CodexBarUsage.Find(Items, "antigravity"))!.Label);
        Assert.Null(CodexBarUsage.AccountFrom(CodexBarUsage.Find(Items, "codex:user@example.com")));
        Assert.Null(CodexBarUsage.AccountFrom(null));
    }

    [Fact]
    public void FailureStatusesSurviveTheStoresExceptionContract()
    {
        ProviderStatus[] statuses =
        [
            new ProviderStatus.NeedsAuth(),
            new ProviderStatus.Unsupported("Not enabled in CodexBar"),
            new ProviderStatus.Error("Claude usage probe timed out.")
        ];
        foreach (var status in statuses)
            Assert.Equal(status, UsageStore.StatusFor(CodexBarUsage.ExceptionFor(status)));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("not json")]
    public void MalformedPayloadsThrow(string json)
    {
        var error = Assert.Throws<UsageProviderException>(() => CodexBarUsage.Parse(json));
        Assert.Equal(UsageErrorKind.BadResponse, error.Kind);
    }
}

public class RemoteAuthPromptTests
{
    private static ProviderSnapshot NeedsAuth(string id, string name, bool remote) =>
        new(id, name, ProviderGlyph.Third, Fidelity.Official, new ProviderStatus.NeedsAuth(), [], IsRemote: remote);

    [Fact]
    public void RemoteProvidersPointAtWsl()
    {
        Assert.Equal("Sign in to Claude inside WSL to read your usage", NeedsAuth("claude", "Claude", true).StatusMessage);
        Assert.Equal("Sign in to Codex inside WSL to read your usage", NeedsAuth("codex", "Codex", true).StatusMessage);
    }

    [Fact]
    public void LocalProvidersKeepTheirPrompts()
    {
        Assert.Equal("Sign in to Cursor in the editor", NeedsAuth("cursor", "Cursor", false).StatusMessage);
        Assert.Equal("Sign in to Codex to read your usage", NeedsAuth("codex", "Codex", false).StatusMessage);
    }
}

public class CodexBarSettingsTests
{
    [Fact]
    public void DefaultsToLocalProvidersAndTheStandardBackend()
    {
        var settings = CodexBarSettings.Resolve(envUrl: null, storedEnabled: false, storedUrl: null, storedProviders: null);
        Assert.False(settings.Enabled);
        // 127.0.0.1, not localhost: Windows resolves localhost to ::1 first, and
        // CodexBar in WSL listens on IPv4 only, so localhost hangs until timeout.
        Assert.Equal(new Uri("http://127.0.0.1:8787"), settings.BaseUrl);
        Assert.Equal(new[] { "claude", "codex", "antigravity", "opencodego", "copilot" }, settings.ProviderIds);
    }

    [Fact]
    public void TheEnvironmentUrlEnablesRemoteModeAndWins()
    {
        var settings = CodexBarSettings.Resolve("http://127.0.0.1:9000", false, "http://localhost:8787", null);
        Assert.True(settings.Enabled);
        Assert.Equal(new Uri("http://127.0.0.1:9000"), settings.BaseUrl);
    }

    [Fact]
    public void StoredSettingsEnableRemoteMode()
    {
        var settings = CodexBarSettings.Resolve(null, true, "http://wsl.local:8787", "Claude, codex\ncopilot,,codex");
        Assert.True(settings.Enabled);
        Assert.Equal(new Uri("http://wsl.local:8787"), settings.BaseUrl);
        Assert.Equal(new[] { "claude", "codex", "copilot" }, settings.ProviderIds);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://localhost:8787")]
    public void AnUnusableUrlFallsBackToTheDefault(string url)
    {
        var settings = CodexBarSettings.Resolve(url, false, null, null);
        Assert.True(settings.Enabled);
        Assert.Equal(new Uri("http://127.0.0.1:8787"), settings.BaseUrl);
    }

    [Fact]
    public void AnEmptyEnvironmentValueIsIgnored() =>
        Assert.False(CodexBarSettings.Resolve("  ", false, null, null).Enabled);
}
