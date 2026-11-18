using System.Net.Http;
using ResearchMissionControl.Application;

namespace ResearchMissionControl.Infrastructure;

// Polite, bounded retry for approved HTTP sources. Public research APIs (Wikipedia,
// Wikidata) rate-limit bursts with HTTP 429, and may return transient 5xx; a single blip
// should not fail a research run. Retries 429/408/5xx and transient transport errors with
// exponential backoff, honouring Retry-After when the server sends it, and finally surfaces
// an InferenceTransportException (same contract as before) when attempts are exhausted.
// The backoff base is injectable so tests run without real waiting.
internal static class HttpResilience
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    public static async Task<string> GetStringAsync(
        HttpClient http,
        string requestUri,
        int maxAttempts,
        TimeSpan baseDelay,
        string sourceLabel,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                // Transient status (e.g. 429 rate-limit, 5xx): back off and retry while budget remains.
                if (IsTransient(code) && attempt < maxAttempts)
                {
                    await Task.Delay(BackoffFor(response, attempt, baseDelay), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                // Non-success status that is non-transient, or the last attempt: fail (no retry).
                if (!response.IsSuccessStatusCode)
                    throw new InferenceTransportException($"{sourceLabel} request failed: HTTP {code}.");
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // Transport-level failure (no response: connection reset, DNS, ...): retry as transient.
                if (attempt >= maxAttempts)
                    throw new InferenceTransportException($"{sourceLabel} request failed: {ex.Message}");
                await Task.Delay(BackoffFor(null, attempt, baseDelay), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(int statusCode) =>
        statusCode is 429 or 408 or 500 or 502 or 503 or 504;

    private static TimeSpan BackoffFor(HttpResponseMessage? response, int attempt, TimeSpan baseDelay)
    {
        // Exponential: baseDelay * 2^(attempt-1), bounded.
        var ticks = baseDelay.Ticks * (1L << (attempt - 1));
        var delay = ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks);

        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > delay)
            delay = delta;
        else if (retryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > delay)
                delay = until;
        }

        if (delay > MaxDelay)
            delay = MaxDelay;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}
