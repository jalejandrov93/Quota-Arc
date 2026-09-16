namespace QuotaArc.Model;

/// Remote mode: read quotas from CodexBar (`codexbar serve`, usually inside
/// WSL) instead of this machine's credential files. Stored in HKCU next to
/// the other preferences; QUOTAARC_CODEXBAR_URL overrides the URL and turns
/// remote mode on by itself.
internal sealed record CodexBarSettings(bool Enabled, Uri BaseUrl, IReadOnlyList<string> ProviderIds)
{
    public const string EnvironmentVariable = "QUOTAARC_CODEXBAR_URL";
    public const string EnabledKey = "codexbarEnabled";
    public const string UrlKey = "codexbarUrl";
    public const string ProvidersKey = "codexbarProviders";

    // 127.0.0.1, not localhost. Windows resolves localhost to ::1 first, while
    // `codexbar serve` in WSL listens on IPv4 only, so a localhost request hangs
    // until it times out instead of falling back — verified on a machine with
    // mirrored networking, where 127.0.0.1:8787 answered in about 100 ms and
    // localhost:8787 failed after 8 seconds.
    public static readonly Uri DefaultUrl = new("http://127.0.0.1:8787");
    public static readonly IReadOnlyList<string> DefaultProviders =
        ["claude", "codex", "antigravity", "opencodego", "copilot"];

    public static CodexBarSettings Load() => Resolve(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        AppSettings.GetBool(EnabledKey),
        AppSettings.Get(UrlKey),
        AppSettings.Get(ProvidersKey));

    public static CodexBarSettings Resolve(string? envUrl, bool storedEnabled, string? storedUrl, string? storedProviders)
    {
        var fromEnvironment = !string.IsNullOrWhiteSpace(envUrl);
        var url = UrlFrom(fromEnvironment ? envUrl : storedUrl) ?? DefaultUrl;
        // Comma- or newline-separated, so both a hand-typed registry value and
        // AppSettings.SetList work.
        var ids = (storedProviders ?? "")
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => id.ToLowerInvariant())
            .Distinct()
            .ToList();
        return new CodexBarSettings(fromEnvironment || storedEnabled, url, ids.Count > 0 ? ids : DefaultProviders);
    }

    private static Uri? UrlFrom(string? raw) =>
        Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : null;
}
