using System.Net.Http;
using System.Text;
using System.Text.Json;
using ResearchMissionControl.Application;

namespace ResearchMissionControl.Infrastructure;

// HTTP client for the authoritative cloud Worker API. Sends the worker bearer credential
// and a fresh Idempotency-Key per call. The cloud owns all state transitions.
public sealed class CloudApiClient : ICloudApiClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _workerId;
    private readonly string _secret;

    public CloudApiClient(string baseUrl, string workerId, string secret, HttpClient? http = null)
    {
        _workerId = workerId;
        _secret = secret;
        var normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        if (http is not null)
        {
            _http = http;
            _ownsHttp = false;
            if (_http.BaseAddress is null)
                _http.BaseAddress = new Uri(normalized, UriKind.Absolute);
        }
        else
        {
            _http = new HttpClient { BaseAddress = new Uri(normalized, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(60) };
            _ownsHttp = true;
        }
    }

    public async Task<ClaimedJob?> ClaimAsync(CancellationToken cancellationToken)
    {
        var body = await PostAsync("api/v1/worker/claims", "{}", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("outcome", out var outcome) || outcome.GetString() != "accepted")
            return null;

        var question = string.Empty;
        var policy = "hybrid-governed";
        if (root.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object)
        {
            if (request.TryGetProperty("question", out var q)) question = q.GetString() ?? string.Empty;
            if (request.TryGetProperty("evidencePolicy", out var ep)) policy = ep.GetString() ?? policy;
        }

        return new ClaimedJob(
            root.GetProperty("jobId").GetString()!,
            root.GetProperty("executionGeneration").GetInt64(),
            root.GetProperty("leaseId").GetString()!,
            question,
            policy,
            root.TryGetProperty("executionProfile", out var p) ? p.GetString() ?? "standard" : "standard");
    }

    public Task StartAsync(string jobId, long generation, string leaseId, CancellationToken cancellationToken) =>
        PostVoid($"api/v1/worker/jobs/{jobId}/start", Lease(leaseId, generation), cancellationToken);

    public Task HeartbeatAsync(string jobId, long generation, string leaseId, CancellationToken cancellationToken) =>
        PostVoid($"api/v1/worker/jobs/{jobId}/heartbeat", Lease(leaseId, generation), cancellationToken);

    public Task CompleteAsync(string jobId, long generation, string leaseId, string disposition, CancellationToken cancellationToken) =>
        PostVoid($"api/v1/worker/jobs/{jobId}/complete", JsonSerializer.Serialize(new { leaseId, generation, disposition }), cancellationToken);

    public Task FailAsync(string jobId, long generation, string leaseId, string reason, CancellationToken cancellationToken) =>
        PostVoid($"api/v1/worker/jobs/{jobId}/fail", JsonSerializer.Serialize(new { leaseId, generation, reason }), cancellationToken);

    private static string Lease(string leaseId, long generation) =>
        JsonSerializer.Serialize(new { leaseId, generation });

    private async Task PostVoid(string path, string json, CancellationToken cancellationToken) =>
        await PostAsync(path, json, cancellationToken).ConfigureAwait(false);

    private async Task<string> PostAsync(string path, string json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_secret}");
        request.Headers.TryAddWithoutValidation("X-Worker-Id", _workerId);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InferenceTransportException($"cloud {path} -> {(int)response.StatusCode}: {body}");
        return body;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
