using System.Text.Json;
using System.Text.Json.Serialization;
using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;

namespace ResearchMissionControl.Evaluation;

public enum ClaimAssessment
{
    Supported,
    Contradicted,
    InsufficientEvidence,
    ConflictingEvidence,
    Unknown,
}

public sealed record ClaimEvaluation(string FindingId, ClaimAssessment Assessment);

// Lane 2 — local-model-assisted semantic assessment of each committed claim against its
// own evidence. A model's assessment is NOT a certificate of truth; uncertainty is
// preserved (anything unparseable or out-of-vocabulary becomes Unknown). Independent of
// execution — it only reads the completed record.
public sealed class SemanticEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IInferenceClient _client;
    private readonly string _modelId;

    public SemanticEvaluator(IInferenceClient client, string modelId)
    {
        _client = client;
        _modelId = modelId;
    }

    public async Task<IReadOnlyList<ClaimEvaluation>> EvaluateAsync(
        IReadOnlyList<Finding> findings,
        Func<string, string?> passageText,
        long generation,
        CancellationToken cancellationToken)
    {
        var results = new List<ClaimEvaluation>();
        foreach (var finding in findings)
        {
            var evidence = string.Join(
                "\n",
                finding.EvidencePassageIds.Select(id => passageText(id) ?? string.Empty).Where(t => t.Length > 0));
            var request = new InferenceRequest(
                "evaluation", generation, _modelId, "evaluator", "semantic",
                Prompt(finding.Statement, evidence), new[] { "assessment" });

            ClaimAssessment assessment;
            try
            {
                var text = await _client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                assessment = Map(Parse(text));
            }
            catch (InferenceTransportException)
            {
                assessment = ClaimAssessment.Unknown;
            }
            catch (TimeoutException)
            {
                assessment = ClaimAssessment.Unknown;
            }

            results.Add(new ClaimEvaluation(finding.FindingId, assessment));
        }
        return results;
    }

    private static string? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<AssessmentDto>(text, JsonOptions)?.Assessment;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClaimAssessment Map(string? value) => value switch
    {
        "supported" => ClaimAssessment.Supported,
        "contradicted" => ClaimAssessment.Contradicted,
        "insufficient_evidence" => ClaimAssessment.InsufficientEvidence,
        "conflicting_evidence" => ClaimAssessment.ConflictingEvidence,
        _ => ClaimAssessment.Unknown,
    };

    private static string Prompt(string statement, string evidence) =>
        "Assess whether the claim is supported by the evidence. Answer with one of: supported, " +
        "contradicted, insufficient_evidence, conflicting_evidence.\n\n" +
        $"Claim: {statement}\n\nEvidence:\n{evidence}\n\n" +
        "Respond with ONLY JSON: {\"assessment\":\"insufficient_evidence\"}";

    private sealed record AssessmentDto(
        [property: JsonPropertyName("assessment")] string? Assessment);
}
