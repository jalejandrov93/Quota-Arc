using System.Net;
using System.Net.Http;
using System.Text.Json;
using QuotaArc.Model;
using QuotaArc.Providers;

namespace QuotaArc.Tests;

public class AgentHubProviderTests : IDisposable
{
    private readonly HttpClient _originalHttp;
    private readonly HttpClient _originalSseHttp;

    public AgentHubProviderTests()
    {
        _originalHttp = AgentHubProvider.Http;
        _originalSseHttp = AgentHubProvider.SseHttp;
    }

    public void Dispose()
    {
        AgentHubProvider.Http = _originalHttp;
        AgentHubProvider.SseHttp = _originalSseHttp;
    }

    [Fact]
    public void IdIsAgentHub()
    {
        var hub = new AgentHubProvider(false);
        Assert.Equal("agenthub", hub.Id);
    }

    [Fact]
    public async Task HealthyResponse_DrawsConcurrencyAndNoBlocks()
    {
        var stateJson = """{"agents":[{"agent":"agy","model":"gpt-4o","status":"ready"}],"jobs":[{"jobId":"j1","agent":"agy","status":"running"},{"jobId":"j2","agent":"agy","status":"queued"}]}""";
        var configJson = """{"breakerState":"closed","overrides":{}}""";

        AgentHubProvider.Http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/api/state")
                return Task.FromResult(FakeHandler.Response(stateJson));
            if (req.RequestUri?.AbsolutePath == "/api/config")
                return Task.FromResult(FakeHandler.Response(configJson));
            return Task.FromResult(FakeHandler.Response("{}"));
        }));

        var hub = new AgentHubProvider(false);
        var snapshot = await hub.FetchSnapshotAsync();

        Assert.IsType<ProviderStatus.Ok>(snapshot.Status);
        Assert.Single(snapshot.Windows);
        Assert.Equal("concurrency", snapshot.Windows[0].Id);
        Assert.Equal(1, snapshot.Windows[0].Used);
        Assert.Null(snapshot.Windows[0].UsedFraction);
        Assert.Equal("1 running, 1 queued", snapshot.Windows[0].Label);
        Assert.Null(snapshot.Block);
    }

    [Fact]
    public async Task OpenBreaker_MapsToBlock()
    {
        var stateJson = """{"agents":[],"jobs":[]}""";
        var configJson = """{"breakerState":"open"}""";

        AgentHubProvider.Http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/api/state")
                return Task.FromResult(FakeHandler.Response(stateJson));
            return Task.FromResult(FakeHandler.Response(configJson));
        }));

        var hub = new AgentHubProvider(false);
        var snapshot = await hub.FetchSnapshotAsync();

        Assert.NotNull(snapshot.Block);
        Assert.Equal("Circuit breaker open", snapshot.Block.Reason);
    }

    [Fact]
    public async Task DegradedAgent_MapsToErrorStatus()
    {
        var stateJson = """{"agents":[{"agent":"agy","model":"gpt-4o","status":"degraded"}],"jobs":[]}""";
        var configJson = """{"breakerState":"closed"}""";

        AgentHubProvider.Http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/api/state")
                return Task.FromResult(FakeHandler.Response(stateJson));
            return Task.FromResult(FakeHandler.Response(configJson));
        }));

        var hub = new AgentHubProvider(false);
        var snapshot = await hub.FetchSnapshotAsync();

        Assert.IsType<ProviderStatus.Error>(snapshot.Status);
        Assert.Contains("degraded", ((ProviderStatus.Error)snapshot.Status).Why);
    }

    [Fact]
    public async Task MalformedResponse_ThrowsBadResponse()
    {
        AgentHubProvider.Http = new HttpClient(new FakeHandler(req => Task.FromResult(FakeHandler.Response("not json"))));
        var hub = new AgentHubProvider(false);

        var ex = await Assert.ThrowsAsync<UsageProviderException>(() => hub.FetchSnapshotAsync());
        Assert.Equal(UsageErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task HubDown_ThrowsNothingMetered()
    {
        AgentHubProvider.Http = new HttpClient(new FakeHandler(req => throw new HttpRequestException("Connection refused")));
        var hub = new AgentHubProvider(false);

        var ex = await Assert.ThrowsAsync<UsageProviderException>(() => hub.FetchSnapshotAsync());
        Assert.Equal(UsageErrorKind.NothingMetered, ex.Kind);
        Assert.Contains("Agent Hub unreachable", ex.Message);
    }
}
