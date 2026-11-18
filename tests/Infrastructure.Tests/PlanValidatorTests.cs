using ResearchMissionControl.Domain;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// Model-free: the application authorizes the model's proposed search queries. PlanValidator
// bounds, trims, and de-duplicates the proposal and guarantees a usable plan, degrading to
// deterministic planning when the model proposes nothing usable.
public sealed class PlanValidatorTests
{
    [Fact]
    public void KeepsDistinctTargetedQueriesAndMarksComparison()
    {
        var plan = PlanValidator.Validate(new[] { "PostgreSQL", "MySQL" }, true, "Compare PostgreSQL and MySQL");

        Assert.Equal(new[] { "PostgreSQL", "MySQL" }, plan.Queries);
        Assert.True(plan.IsComparison);
    }

    [Fact]
    public void TrimsCollapsesWhitespaceAndDropsCaseInsensitiveDuplicates()
    {
        var plan = PlanValidator.Validate(new[] { "  Kafka ", "kafka", "Rabbit\nMQ" }, false, "q");

        Assert.Equal(new[] { "Kafka", "Rabbit MQ" }, plan.Queries);
    }

    [Fact]
    public void CapsTheNumberOfQueries()
    {
        var plan = PlanValidator.Validate(new[] { "a", "b", "c", "d", "e" }, false, "q");

        Assert.Equal(PlanValidator.MaxQueries, plan.Queries.Count);
        Assert.Equal(new[] { "a", "b", "c" }, plan.Queries);
    }

    [Fact]
    public void TruncatesAnOverlongQuery()
    {
        var plan = PlanValidator.Validate(new[] { new string('x', 200) }, false, "q");

        Assert.True(plan.Queries[0].Length <= PlanValidator.MaxQueryChars);
    }

    [Fact]
    public void EmptyProposalFallsBackToDeterministicSingleQuery()
    {
        var plan = PlanValidator.Validate(System.Array.Empty<string>(), true, "What is photosynthesis?");

        Assert.Single(plan.Queries);
        Assert.Equal("photosynthesis", plan.Queries[0]); // QueryPlanner fallback
        Assert.False(plan.IsComparison);                 // a single query is never a comparison
    }

    [Fact]
    public void NullProposalFallsBackToDeterministicSingleQuery()
    {
        var plan = PlanValidator.Validate(null, false, "What is TLS?");

        Assert.Equal(new[] { "TLS" }, plan.Queries);
        Assert.False(plan.IsComparison);
    }

    [Fact]
    public void BlankAndNullEntriesAreIgnored()
    {
        var plan = PlanValidator.Validate(new[] { "", "   ", null!, "Kafka" }, false, "alternatives to Kafka");

        Assert.Equal(new[] { "Kafka" }, plan.Queries);
    }

    [Fact]
    public void SingleSurvivingQueryIsNeverAComparison()
    {
        // The model claimed a comparison but only one usable query remains.
        var plan = PlanValidator.Validate(new[] { "Kafka", "   " }, true, "alternatives to Kafka");

        Assert.Single(plan.Queries);
        Assert.False(plan.IsComparison);
    }
}
