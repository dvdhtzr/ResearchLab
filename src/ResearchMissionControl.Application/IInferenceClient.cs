using ResearchMissionControl.Domain;

namespace ResearchMissionControl.Application;

// Port for raw local model invocation. Implemented by the real Ollama adapter (M3-b)
// and by test fakes. Keeping the volatile external SDK surface behind this port means
// the gateway logic (and its model-free tests) never depend on the SDK's exact API.
public interface IInferenceClient
{
    // Returns the raw model text (expected to be JSON). Throws InferenceTransportException
    // on transport failure, TimeoutException on timeout, and honors cancellation.
    Task<string> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken);

    // The digest Ollama reports for an installed model, or null if it is not installed.
    Task<string?> GetInstalledDigestAsync(string modelId, CancellationToken cancellationToken);
}

public sealed class InferenceTransportException : Exception
{
    public InferenceTransportException(string message) : base(message)
    {
    }
}
