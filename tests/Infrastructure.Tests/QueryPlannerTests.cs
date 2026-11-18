using ResearchMissionControl.Domain;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// Model-free: deterministic query planning extracts salient terms from a question.
public sealed class QueryPlannerTests
{
    [Theory]
    [InlineData("What is photosynthesis?", "photosynthesis")]
    [InlineData("suggest alternatives to azure service bus", "azure service bus")]
    [InlineData("What are the alternatives to Kafka?", "Kafka")]
    [InlineData("Compare SQL Server and PostgreSQL", "SQL Server PostgreSQL")]
    [InlineData("How does TLS work?", "TLS work")]
    public void ExtractsSalientTerms(string question, string expected)
    {
        Assert.Equal(expected, QueryPlanner.PlanQuery(question));
    }

    [Fact]
    public void FallsBackToTheQuestionWhenNothingSalientRemains()
    {
        Assert.Equal("what is the", QueryPlanner.PlanQuery("what is the"));
    }
}
