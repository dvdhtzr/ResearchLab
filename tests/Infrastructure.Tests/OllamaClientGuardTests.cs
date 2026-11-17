using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// Model-free security test: the Ollama client must refuse any non-loopback host, so
// Ollama is never reachable over a network address (spec §20). No Ollama required.
public sealed class OllamaClientGuardTests
{
    [Theory]
    [InlineData("http://evil.example.com")]
    [InlineData("https://203.0.113.5:11434")]
    [InlineData("http://10.0.0.5:11434")]
    public void RejectsNonLoopbackHost(string url)
    {
        Assert.Throws<ArgumentException>(() => new OllamaInferenceClient(url));
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    public void AcceptsLoopbackHost(string url)
    {
        using var client = new OllamaInferenceClient(url);
        Assert.NotNull(client);
    }
}
