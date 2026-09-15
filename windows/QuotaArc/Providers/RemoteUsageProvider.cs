using QuotaArc.Model;

namespace QuotaArc.Providers;

/// A ring read from CodexBar (usually `codexbar serve` inside WSL) instead of
/// this machine's credential files. The key is the CodexBar provider id, or
/// `provider:account` when CodexBar serves several accounts for one provider.
internal sealed class RemoteUsageProvider : IUsageProvider
{
    private readonly CodexBarClient _client;
    private CodexBarItem? _lastReading;

    public RemoteUsageProvider(string key, CodexBarClient client)
    {
        _client = client;
        Id = key;
        var (provider, account) = CodexBarUsage.SplitKey(key);
        DisplayName = CodexBarUsage.DisplayName(provider, account);
        Glyph = CodexBarUsage.GlyphFor(provider);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public ProviderGlyph Glyph { get; }
    public bool IsRemote => true;
    public SignInRoute SignInRoute { get; } =
        new SignInRoute.Guidance("Sign in inside WSL with the provider's CLI, then refresh.");

    public ProviderAccount? Account() => CodexBarUsage.AccountFrom(_lastReading);

    public async Task<ProviderSnapshot> FetchSnapshotAsync()
    {
        var items = await _client.UsageAsync();
        var snapshot = CodexBarUsage.Snapshot(Id, items);
        if (snapshot.Status is ProviderStatus.Ok)
        {
            _lastReading = CodexBarUsage.Find(items, Id);
            return snapshot;
        }
        // Thrown rather than returned: a returned snapshot would replace the
        // last good reading, while UsageStore only drops it for NeedsAuth and
        // Unsupported — the same contract every local provider gets.
        if (snapshot.Status is ProviderStatus.NeedsAuth or ProviderStatus.Unsupported)
            _lastReading = null;
        throw CodexBarUsage.ExceptionFor(snapshot.Status);
    }

    /// Builds the remote rings for startup. CodexBar is asked once, briefly,
    /// so accounts it already serves become their own rings; if it is slow or
    /// down the configured ids are used as-is and the request keeps running
    /// in the shared client for the store's first refresh to join.
    public static List<IUsageProvider> ForSettings(CodexBarSettings settings, TimeSpan wait)
    {
        var client = new CodexBarClient(settings.BaseUrl);
        IReadOnlyList<CodexBarItem>? known = null;
        try
        {
            var first = Task.Run(() => client.UsageAsync());
            if (first.Wait(wait)) known = first.Result;
        }
        catch (Exception ex)
        {
            QuotaArc.Log.Error("codexbar: " + ex.GetBaseException().Message);
        }
        var keys = CodexBarUsage.ProviderKeys(settings.ProviderIds, known);
        QuotaArc.Log.Info($"codexbar {client.UsageUri}: {keys.Count} rings for " +
            string.Join(", ", keys.Select(k => CodexBarUsage.SplitKey(k).Provider).Distinct()));
        return keys.Select(k => (IUsageProvider)new RemoteUsageProvider(k, client)).ToList();
    }
}
