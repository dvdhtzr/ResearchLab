namespace ResearchMissionControl.Domain;

// Governed tools (spec §23). Agents submit typed proposals; the host validates and
// authorizes each one. There is no unrestricted automatic function invocation.
public sealed record ToolProposal(string Tool, string ArgumentJson);

public sealed record ToolAuthorization(bool Authorized, string? Reason);

public sealed class ToolRegistry
{
    private readonly IReadOnlySet<string> _tools;

    public ToolRegistry(IEnumerable<string> tools) =>
        _tools = new HashSet<string>(tools, StringComparer.Ordinal);

    public static ToolRegistry Default() => new(new[]
    {
        "source.search.v1",
        "source.fetch.v1",
        "evidence.find.v1",
        "evidence.read.v1",
    });

    public bool IsRegistered(string tool) => _tools.Contains(tool);

    public ToolAuthorization Authorize(ToolProposal proposal)
    {
        if (!_tools.Contains(proposal.Tool))
            return new ToolAuthorization(false, "tool_not_registered");
        if (string.IsNullOrWhiteSpace(proposal.ArgumentJson))
            return new ToolAuthorization(false, "missing_arguments");
        return new ToolAuthorization(true, null);
    }
}

public enum HostDecision
{
    Revise,
    Conclude,
}

public static class ResearchHost
{
    // Deterministic host decision: revise when the skeptic raised challenges and the
    // round budget still allows another round; otherwise conclude.
    public static HostDecision Decide(int challengeCount, int roundsUsed, int maxRounds) =>
        challengeCount > 0 && roundsUsed < maxRounds ? HostDecision.Revise : HostDecision.Conclude;
}
