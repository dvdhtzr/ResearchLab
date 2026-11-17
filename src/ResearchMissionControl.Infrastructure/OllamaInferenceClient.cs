using System.Net.Http;
using System.Text;
using System.Text.Json;
using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;

namespace ResearchMissionControl.Infrastructure;

// Real local-inference client. Talks to Ollama's REST API directly (stable surface),
// avoiding a volatile SDK dependency. SECURITY: refuses any non-loopback host, so Ollama
// is never reached over a network address. No cloud fallback.
public sealed class OllamaInferenceClient : IInferenceClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public OllamaInferenceClient(string baseUrl = "http://localhost:11434", HttpClient? http = null)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        if (!IsLoopback(uri))
            throw new ArgumentException(
                $"Ollama must be local; refusing non-loopback host '{uri.Host}'.", nameof(baseUrl));

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
            _http = new HttpClient { BaseAddress = new Uri(normalized, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(120) };
            _ownsHttp = true;
        }
    }

    private static bool IsLoopback(Uri uri)
    {
        var host = uri.Host.Trim('[', ']');
        return uri.IsLoopback
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "::1";
    }

    public async Task<string?> GetInstalledDigestAsync(string modelId, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            using var response = await _http.GetAsync("api/tags", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InferenceTransportException("Timed out listing Ollama models.");
        }
        catch (HttpRequestException ex)
        {
            throw new InferenceTransportException(ex.Message);
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var model in models.EnumerateArray())
        {
            if (model.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), modelId, StringComparison.Ordinal))
            {
                return model.TryGetProperty("digest", out var digest) ? digest.GetString() : null;
            }
        }
        return null;
    }

    public async Task<string> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = request.ModelId,
            prompt = request.Prompt,
            stream = false,
            format = "json",
        });

        string body;
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("api/generate", content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Ollama generate timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new InferenceTransportException(ex.Message);
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("response", out var responseText) ||
            responseText.ValueKind != JsonValueKind.String)
        {
            // Ollama returned an unexpected envelope; surface as transport error, not a fake result.
            throw new InferenceTransportException("Ollama response envelope missing 'response'.");
        }
        return responseText.GetString() ?? string.Empty;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
