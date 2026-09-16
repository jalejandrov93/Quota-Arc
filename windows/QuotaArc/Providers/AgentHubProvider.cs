using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaArc.Model;

namespace QuotaArc.Providers;

using System.IO;

internal sealed class AgentHubProvider : IUsageProvider
{
    internal static HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    internal static HttpClient SseHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private const string BaseUrl = "http://127.0.0.1:7777";

    public Action? OnSseEvent;
    private CancellationTokenSource? _sseCts;
    private Task? _sseTask;

    public AgentHubProvider(bool startSse = true)
    {
        if (startSse) StartSse();
    }

    public string Id => "agenthub";
    public string DisplayName => "Agent Hub";
    public ProviderGlyph Glyph => ProviderGlyph.Agenthub;
    public SignInRoute SignInRoute => new SignInRoute.Guidance("Run Agent Hub to read state");

    public ProviderAccount? Account()
    {
        return new ProviderAccount(null, null, "Agent Hub", null);
    }

    private void StartSse()
    {
        _sseCts?.Cancel();
        _sseCts = new CancellationTokenSource();
        _sseTask = Task.Run(() => SseLoopAsync(_sseCts.Token));
    }

    private async Task SseLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/events");
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                using var response = await SseHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream);

                while (!token.IsCancellationRequested && !reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line) && line.StartsWith("data: "))
                    {
                        // Found a live event, notify listeners (e.g. App.xaml.cs) to trigger a refresh
                        OnSseEvent?.Invoke();
                    }
                }
            }
            catch
            {
                // Wait before retrying the SSE connection
                try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
                catch { break; }
            }
        }
    }

    public async Task<ProviderSnapshot> FetchSnapshotAsync()
    {
        try
        {
            var stateTask = Http.GetFromJsonAsync<AgentHubState>($"{BaseUrl}/api/state");
            var configTask = Http.GetFromJsonAsync<AgentHubConfig>($"{BaseUrl}/api/config");

            await Task.WhenAll(stateTask, configTask);

            var state = await stateTask;
            var config = await configTask;

            if (state is null || config is null)
                throw UsageProviderException.BadResponse(0);

            var runningJobs = state.Jobs?.Count(j => j.Status == "running") ?? 0;
            var queuedJobs = state.Jobs?.Count(j => j.Status == "queued") ?? 0;
            var jobsTotal = runningJobs + queuedJobs;

            var windows = new List<LimitWindow>
            {
                new LimitWindow("concurrency", jobsTotal > 0 ? $"{runningJobs} running, {queuedJobs} queued" : "No active jobs", Used: runningJobs)
            };

            UsageBlock? block = null;
            if (config.BreakerState == "open")
            {
                block = new UsageBlock("Circuit breaker open", null);
            }
            else if (config.Overrides?.ValueKind == JsonValueKind.Object && config.Overrides.Value.EnumerateObject().Any())
            {
                block = new UsageBlock("Agent held by human", null);
            }

            ProviderStatus status = new ProviderStatus.Ok();
            if (state.Agents is { Length: > 0 } agents)
            {
                var unavailable = agents.Any(a => a.Status == "unavailable");
                var degraded = agents.Any(a => a.Status == "degraded");

                if (unavailable)
                    status = new ProviderStatus.Error("One or more agents are unavailable");
                else if (degraded)
                    status = new ProviderStatus.Error("One or more agents are degraded");
            }

            return new ProviderSnapshot(
                Id, DisplayName, Glyph, Fidelity.Derived, status, windows,
                windows.FirstOrDefault()?.Id, block);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Specifically mapped to NothingMetered to show a clear unsupported message on the cell rather than an error toast
            throw UsageProviderException.NothingMetered("Agent Hub unreachable. If in WSL, ensure mirrored networking is enabled.");
        }
        catch (JsonException)
        {
            throw UsageProviderException.BadResponse(0);
        }
    }
}

internal sealed record AgentHubState(
    [property: JsonPropertyName("agents")] AgentHubAgent[]? Agents,
    [property: JsonPropertyName("jobs")] AgentHubJob[]? Jobs
);

internal sealed record AgentHubAgent(
    [property: JsonPropertyName("agent")] string? Agent,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("reason")] string? Reason
);

internal sealed record AgentHubJob(
    [property: JsonPropertyName("jobId")] string? JobId,
    [property: JsonPropertyName("agent")] string? Agent,
    [property: JsonPropertyName("status")] string? Status
);

internal sealed record AgentHubConfig(
    [property: JsonPropertyName("breakerState")] string? BreakerState,
    [property: JsonPropertyName("overrides")] JsonElement? Overrides
);
