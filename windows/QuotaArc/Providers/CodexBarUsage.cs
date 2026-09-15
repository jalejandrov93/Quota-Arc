using System.Globalization;
using System.Text.Json;
using QuotaArc.Model;

namespace QuotaArc.Providers;

internal sealed record CodexBarItem(
    string? Provider,
    string? Account,
    string? Source,
    CodexBarPayload? Usage,
    CodexBarError? Error);

internal sealed record CodexBarPayload(
    CodexBarWindow? Primary,
    CodexBarWindow? Secondary,
    CodexBarWindow? Tertiary,
    List<CodexBarExtraWindow>? ExtraRateWindows,
    CodexBarIdentity? Identity,
    string? LoginMethod);

// ResetsAt stays a raw element so one provider sending an unexpected shape
// can't make the whole shared payload unreadable.
internal sealed record CodexBarWindow(double? UsedPercent, double? WindowMinutes, JsonElement? ResetsAt);

internal sealed record CodexBarExtraWindow(string? Id, string? Title, CodexBarWindow? Window);

internal sealed record CodexBarIdentity(string? AccountEmail, string? LoginMethod);

internal sealed record CodexBarError(string? Kind, string? Message);

/// Maps CodexBar's `GET /usage` payload onto the rings. Pure, so every rule
/// here is covered without a running backend.
internal static class CodexBarUsage
{
    public const string AccountSource = "CodexBar (WSL)";
    public const string NotEnabled = "Not enabled in CodexBar";
    public const int MaxErrorLength = 140;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] AuthHints = ["401", "unauthorized", "sign in", "login", "not configured", "token"];

    public static IReadOnlyList<CodexBarItem> Parse(string json)
    {
        List<CodexBarItem?>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<CodexBarItem?>>(json, Json);
        }
        catch (JsonException)
        {
            throw Malformed();
        }
        if (items is null) throw Malformed();
        return items.OfType<CodexBarItem>().Where(i => !string.IsNullOrEmpty(i.Provider)).ToList();
    }

    public static ProviderSnapshot Snapshot(string key, IReadOnlyList<CodexBarItem> items)
    {
        var (provider, account) = SplitKey(key);
        var snapshot = new ProviderSnapshot(
            key, DisplayName(provider, account), GlyphFor(provider), Fidelity.Official,
            new ProviderStatus.Ok(), [], IsRemote: true);

        var item = Find(items, key);
        if (item is null)
            return snapshot with { Status = new ProviderStatus.Unsupported(NotEnabled) };
        if (item.Usage is { } usage && Windows(usage) is { Count: > 0 } windows)
            return snapshot with { Windows = windows, HeadlineId = windows[0].Id };
        if (item.Error is { } error)
            return snapshot with { Status = StatusForError(error.Message) };
        return snapshot with { Status = new ProviderStatus.Unsupported("CodexBar reported no usage windows") };
    }

    /// Primary, secondary and tertiary first (primary is the headline), then
    /// the provider's extra windows under their own titles.
    public static List<LimitWindow> Windows(CodexBarPayload usage)
    {
        var onlyPrimary = usage.Secondary is null && usage.Tertiary is null;
        var windows = new List<LimitWindow>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string id, string label, CodexBarWindow window)
        {
            if (window.UsedPercent is not { } percent || !ids.Add(id)) return;
            windows.Add(new LimitWindow(
                id, UniqueLabel(label, labels), Math.Clamp(percent / 100, 0, 1),
                ResetsAt: ResetTime(window.ResetsAt)));
        }

        foreach (var (id, window) in new[] { ("primary", usage.Primary), ("secondary", usage.Secondary), ("tertiary", usage.Tertiary) })
            if (window is not null) Add(id, WindowLabel(window.WindowMinutes, onlyPrimary), window);
        foreach (var extra in usage.ExtraRateWindows ?? Enumerable.Empty<CodexBarExtraWindow>())
            if (extra.Window is not null && extra.Id is { Length: > 0 } id)
                Add(id, extra.Title is { Length: > 0 } title ? title : id, extra.Window);
        return windows;
    }

    public static string WindowLabel(double? minutes, bool onlyPrimary)
    {
        // CodexBar leaves the length out for billing-period quotas (Copilot).
        if (minutes is not { } m || m <= 0) return onlyPrimary ? "Monthly" : "Quota";
        var whole = (long)Math.Round(m);
        return whole switch
        {
            300 => "Session",
            1440 => "Daily",
            10080 => "Weekly",
            43200 => "Monthly",
            _ when whole % 1440 == 0 => $"{whole / 1440}d",
            _ when whole % 60 == 0 => $"{whole / 60}h",
            _ => $"{whole}m"
        };
    }

    public static ProviderStatus StatusForError(string? message)
    {
        var text = message ?? "";
        if (AuthHints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            return new ProviderStatus.NeedsAuth();
        var line = text.Split('\n', 2)[0].Trim();
        if (line.Length == 0) line = "CodexBar reported an error";
        if (line.Length > MaxErrorLength) line = line[..(MaxErrorLength - 1)] + "…";
        return new ProviderStatus.Error(line);
    }

    /// The failure a remote provider throws so UsageStore treats it like any
    /// local one: NeedsAuth and Unsupported clear history, errors keep the
    /// last good reading.
    public static UsageProviderException ExceptionFor(ProviderStatus status) => status switch
    {
        ProviderStatus.NeedsAuth => UsageProviderException.NeedsAuth(),
        ProviderStatus.AccessDenied => UsageProviderException.AccessDenied(),
        ProviderStatus.Unsupported u => UsageProviderException.NothingMetered(u.Why),
        ProviderStatus.Error e => new UsageProviderException(UsageErrorKind.BadResponse, e.Why),
        _ => new UsageProviderException(UsageErrorKind.BadResponse, "CodexBar reported an unexpected status")
    };

    public static ProviderGlyph GlyphFor(string provider) => provider.ToLowerInvariant() switch
    {
        "claude" => ProviderGlyph.Claude,
        "codex" => ProviderGlyph.Openai,
        "cursor" => ProviderGlyph.Cursor,
        "antigravity" or "gemini" => ProviderGlyph.Antigravity,
        "glm" or "zai" => ProviderGlyph.Glm,
        "grok" => ProviderGlyph.Grok,
        var id when id.StartsWith("opencode", StringComparison.Ordinal) => ProviderGlyph.Opencode,
        _ => ProviderGlyph.Third
    };

    public static string DisplayName(string provider, string? account)
    {
        var name = provider.ToLowerInvariant() switch
        {
            "claude" => "Claude",
            "codex" => "Codex",
            "antigravity" => "Antigravity",
            "opencodego" => "OpenCode Go",
            "copilot" => "Copilot",
            "" => provider,
            _ => char.ToUpperInvariant(provider[0]) + provider[1..]
        };
        return account is { Length: > 0 } ? $"{name} · {account}" : name;
    }

    public static (string Provider, string? Account) SplitKey(string key)
    {
        var colon = key.IndexOf(':');
        return colon < 0 ? (key, null) : (key[..colon], NullIfEmpty(key[(colon + 1)..]));
    }

    public static string KeyFor(string provider, string? account) =>
        account is { Length: > 0 } ? $"{provider}:{account}" : provider;

    /// An exact account match first. A key without an account falls back to
    /// the provider's first entry, so a ring created before CodexBar listed
    /// its accounts still shows something.
    public static CodexBarItem? Find(IReadOnlyList<CodexBarItem> items, string key)
    {
        var (provider, account) = SplitKey(key);
        var sameProvider = items
            .Where(i => string.Equals(i.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return sameProvider.FirstOrDefault(i => NullIfEmpty(i.Account) == account)
            ?? (account is null ? sameProvider.FirstOrDefault() : null);
    }

    /// One ring per enabled provider, or one per account when CodexBar serves
    /// several for the same provider. Without a payload the ids are used as-is.
    public static IReadOnlyList<string> ProviderKeys(IEnumerable<string> enabledIds, IReadOnlyList<CodexBarItem>? items)
    {
        var keys = new List<string>();
        foreach (var id in enabledIds)
        {
            var accounts = (items ?? Array.Empty<CodexBarItem>())
                .Where(i => string.Equals(i.Provider, id, StringComparison.OrdinalIgnoreCase))
                .Select(i => NullIfEmpty(i.Account))
                .Distinct()
                .ToList();
            if (accounts.All(a => a is null)) keys.Add(id);
            else keys.AddRange(accounts.Select(a => KeyFor(id, a)));
        }
        return keys.Distinct().ToList();
    }

    public static ProviderAccount? AccountFrom(CodexBarItem? item)
    {
        if (item?.Usage is not { } usage) return null;
        return new ProviderAccount(
            NullIfEmpty(usage.Identity?.AccountEmail) ?? NullIfEmpty(item.Account),
            NullIfEmpty(usage.Identity?.LoginMethod) ?? NullIfEmpty(usage.LoginMethod),
            AccountSource,
            null);
    }

    private static string UniqueLabel(string label, HashSet<string> taken)
    {
        var candidate = label;
        for (var n = 2; !taken.Add(candidate); n++)
            candidate = $"{label} {n}";
        return candidate;
    }

    private static DateTime? ResetTime(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.String } value) return null;
        return DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var at)
            ? at.LocalDateTime
            : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static UsageProviderException Malformed() =>
        new(UsageErrorKind.BadResponse, "CodexBar sent a response Quota Arc couldn't read");
}
