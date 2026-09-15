using System.Net;
using System.Net.Http;
using System.Text;
using QuotaArc.Model;
using QuotaArc.Providers;

namespace QuotaArc.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _calls;
    public int Calls => _calls;
    public List<Uri?> Requested { get; } = [];

    public static FakeHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => Task.FromResult(Response(body, status)));

    public static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (Requested) Requested.Add(request.RequestUri);
        return respond(request);
    }
}

public class CodexBarClientTests
{
    private static readonly Uri Base = new("http://localhost:8787");
    private DateTime _now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Local);

    private CodexBarClient Client(FakeHandler handler, Uri? baseUrl = null) =>
        new(baseUrl ?? Base, new HttpClient(handler), () => _now);

    [Theory]
    [InlineData("http://localhost:8787")]
    [InlineData("http://localhost:8787/")]
    public async Task RequestsTheUsageEndpoint(string baseUrl)
    {
        var handler = FakeHandler.Json(CodexBarFixture.Usage);
        var items = await Client(handler, new Uri(baseUrl)).UsageAsync();
        Assert.Equal(7, items.Count);
        Assert.Equal(new Uri("http://localhost:8787/usage"), Assert.Single(handler.Requested));
    }

    [Fact]
    public async Task ConcurrentFetchesShareOneRequest()
    {
        var release = new TaskCompletionSource();
        var handler = new FakeHandler(async _ =>
        {
            await release.Task;
            return FakeHandler.Response(CodexBarFixture.Usage);
        });
        var client = Client(handler);

        var pending = Enumerable.Range(0, 5).Select(_ => client.UsageAsync()).ToList();
        release.SetResult();
        var results = await Task.WhenAll(pending);

        Assert.Equal(1, handler.Calls);
        Assert.All(results, r => Assert.Equal(7, r.Count));
    }

    [Fact]
    public async Task ACachedReadingIsReusedUntilItExpires()
    {
        var handler = FakeHandler.Json(CodexBarFixture.Usage);
        var client = Client(handler);

        await client.UsageAsync();
        _now = _now.AddSeconds(10);
        await client.UsageAsync();
        Assert.Equal(1, handler.Calls);

        _now = _now.Add(CodexBarClient.CacheTtl);
        await client.UsageAsync();
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AnUnreachableBackendThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("Connection refused"));
        var error = await Assert.ThrowsAsync<UsageProviderException>(() => Client(handler).UsageAsync());
        Assert.Equal(UsageErrorKind.BadResponse, error.Kind);
        Assert.Contains("CodexBar", error.Message);
    }

    [Fact]
    public async Task ATimeoutThrows()
    {
        var handler = new FakeHandler(_ => throw new TaskCanceledException("timed out"));
        var error = await Assert.ThrowsAsync<UsageProviderException>(() => Client(handler).UsageAsync());
        Assert.Equal(UsageErrorKind.BadResponse, error.Kind);
    }

    [Fact]
    public async Task AServerErrorThrows()
    {
        var handler = FakeHandler.Json("oops", HttpStatusCode.InternalServerError);
        var error = await Assert.ThrowsAsync<UsageProviderException>(() => Client(handler).UsageAsync());
        Assert.Equal(UsageErrorKind.BadResponse, error.Kind);
        Assert.Equal(500, error.Status);
    }

    [Fact]
    public async Task MalformedJsonThrows()
    {
        var handler = FakeHandler.Json("{ \"not\": \"an array\" ");
        var error = await Assert.ThrowsAsync<UsageProviderException>(() => Client(handler).UsageAsync());
        Assert.Equal(UsageErrorKind.BadResponse, error.Kind);
    }

    [Fact]
    public async Task AFailureIsSharedBrieflyThenRetried()
    {
        var fail = true;
        var handler = new FakeHandler(_ => Task.FromResult(fail
            ? FakeHandler.Response("down", HttpStatusCode.BadGateway)
            : FakeHandler.Response(CodexBarFixture.Usage)));
        var client = Client(handler);

        await Assert.ThrowsAsync<UsageProviderException>(() => client.UsageAsync());
        await Assert.ThrowsAsync<UsageProviderException>(() => client.UsageAsync());
        Assert.Equal(1, handler.Calls);

        fail = false;
        _now = _now.Add(CodexBarClient.FailureTtl);
        Assert.Equal(7, (await client.UsageAsync()).Count);
        Assert.Equal(2, handler.Calls);
    }
}

public class RemoteUsageProviderTests
{
    private static RemoteUsageProvider Provider(string key) =>
        new(key, new CodexBarClient(new Uri("http://localhost:8787"),
            new HttpClient(FakeHandler.Json(CodexBarFixture.Usage))));

    [Fact]
    public async Task FetchReturnsTheMappedSnapshot()
    {
        var provider = Provider("claude");
        Assert.Null(provider.Account());

        var snap = await provider.FetchSnapshotAsync();

        Assert.IsType<ProviderStatus.Ok>(snap.Status);
        Assert.Equal(3, snap.Windows.Count);
        Assert.True(snap.IsRemote);
        Assert.Equal("user@example.com", provider.Account()?.Label);
    }

    [Fact]
    public async Task ASignedOutAccountThrowsNeedsAuth()
    {
        var error = await Assert.ThrowsAsync<UsageProviderException>(
            () => Provider("codex:user@example.com").FetchSnapshotAsync());
        Assert.Equal(UsageErrorKind.NeedsAuth, error.Kind);
    }

    [Fact]
    public async Task AProviderCodexBarDoesNotServeIsUnsupported()
    {
        var error = await Assert.ThrowsAsync<UsageProviderException>(() => Provider("cursor").FetchSnapshotAsync());
        Assert.Equal(new ProviderStatus.Unsupported("Not enabled in CodexBar"), UsageStore.StatusFor(error));
    }

    [Fact]
    public async Task ACodexBarFailureKeepsItsMessage()
    {
        var error = await Assert.ThrowsAsync<UsageProviderException>(() => Provider("opencode").FetchSnapshotAsync());
        Assert.Equal(
            new ProviderStatus.Error("Error: selected source requires web support and is only supported on macOS."),
            UsageStore.StatusFor(error));
    }

    [Fact]
    public void IdentityFollowsTheKey()
    {
        IUsageProvider provider = Provider("codex:user2@example.com");
        Assert.Equal("codex:user2@example.com", provider.Id);
        Assert.Equal("Codex · user2@example.com", provider.DisplayName);
        Assert.Equal(ProviderGlyph.Openai, provider.Glyph);
        Assert.True(provider.IsRemote);
        var guidance = Assert.IsType<SignInRoute.Guidance>(provider.SignInRoute);
        Assert.Equal("Sign in inside WSL with the provider's CLI, then refresh.", guidance.Text);
    }
}
