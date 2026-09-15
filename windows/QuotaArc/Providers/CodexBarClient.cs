namespace QuotaArc.Providers;

/// One shared reader for `codexbar serve`. Every remote ring reads the same
/// /usage payload, so a refresh over N rings costs one request: callers that
/// arrive mid-request join it, and a finished reading is reused for CacheTtl.
/// Failures are shared too, but only for FailureTtl — long enough that a
/// backend that is down fails a refresh cycle once instead of once per ring.
internal sealed class CodexBarClient
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private Task<IReadOnlyList<CodexBarItem>>? _current;
    private DateTime _expiresAt;

    public CodexBarClient(Uri baseUrl, HttpClient? http = null, Func<DateTime>? now = null)
    {
        BaseUrl = baseUrl;
        UsageUri = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/usage");
        // CodexBar probes every enabled provider before answering, so this is
        // deliberately more patient than the direct provider calls.
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _now = now ?? (() => DateTime.Now);
    }

    public Uri BaseUrl { get; }
    public Uri UsageUri { get; }

    public Task<IReadOnlyList<CodexBarItem>> UsageAsync()
    {
        lock (_gate)
        {
            if (_current is { } current && (!current.IsCompleted || _now() < _expiresAt))
                return current;
            _current = FetchAndStampAsync();
            return _current;
        }
    }

    private async Task<IReadOnlyList<CodexBarItem>> FetchAndStampAsync()
    {
        try
        {
            var items = await FetchAsync().ConfigureAwait(false);
            Stamp(CacheTtl);
            return items;
        }
        catch
        {
            Stamp(FailureTtl);
            throw;
        }
    }

    private void Stamp(TimeSpan ttl)
    {
        lock (_gate) _expiresAt = _now() + ttl;
    }

    private async Task<IReadOnlyList<CodexBarItem>> FetchAsync()
    {
        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUri);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            var status = (int)res.StatusCode;
            if (status is < 200 or >= 300)
                throw new UsageProviderException(UsageErrorKind.BadResponse, $"CodexBar returned HTTP {status}", status);
            body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new UsageProviderException(
                UsageErrorKind.BadResponse, $"CodexBar unreachable at {BaseUrl.GetLeftPart(UriPartial.Authority)}");
        }
        return CodexBarUsage.Parse(body);
    }
}
