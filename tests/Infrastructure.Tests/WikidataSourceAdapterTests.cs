using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// Model-free: Wikidata response parsing and fact rendering, exercised against canned JSON
// (no network). The live fetch orchestration is covered by the gated Integration tests.
public sealed class WikidataSourceAdapterTests
{
    private const string EntityJson = """
    {
      "entities": {
        "Q1": {
          "labels": { "en": { "language": "en", "value": "PostgreSQL" } },
          "descriptions": { "en": { "language": "en", "value": "open-source relational database management system" } },
          "aliases": { "en": [ { "language": "en", "value": "Postgres" } ] },
          "claims": {
            "P31":  [ { "mainsnak": { "snaktype": "value", "property": "P31",  "datavalue": { "value": { "entity-type": "item", "id": "Q176165" }, "type": "wikibase-entityid" } } } ],
            "P571": [ { "mainsnak": { "snaktype": "value", "property": "P571", "datavalue": { "value": { "time": "+1996-07-08T00:00:00Z", "precision": 11 }, "type": "time" } } } ],
            "P277": [ { "mainsnak": { "snaktype": "value", "property": "P277", "datavalue": { "value": { "entity-type": "item", "id": "Q15777" }, "type": "wikibase-entityid" } } } ],
            "P348": [ { "mainsnak": { "snaktype": "value", "property": "P348", "datavalue": { "value": "16.1", "type": "string" } } } ],
            "P0":   [ { "mainsnak": { "snaktype": "novalue", "property": "P0" } } ]
          }
        }
      }
    }
    """;

    private const string LabelsJson = """
    {
      "entities": {
        "P31":     { "labels": { "en": { "value": "instance of" } } },
        "Q176165": { "labels": { "en": { "value": "relational database management system" } } },
        "P571":    { "labels": { "en": { "value": "inception" } } },
        "P277":    { "labels": { "en": { "value": "programmed in" } } },
        "Q15777":  { "labels": { "en": { "value": "C" } } },
        "P348":    { "labels": { "en": { "value": "software version identifier" } } }
      }
    }
    """;

    [Fact]
    public void ParseSearchHits_MapsEntitiesToWikidataUrls()
    {
        const string json = """
        { "search": [
            { "id": "Q192490", "label": "PostgreSQL", "description": "database" },
            { "id": "Q850", "label": "MySQL", "description": "database" }
        ] }
        """;

        var hits = WikidataSourceAdapter.ParseSearchHits(json, 5);

        Assert.Equal(2, hits.Count);
        Assert.Equal("https://www.wikidata.org/wiki/Q192490", hits[0].Url);
        Assert.Equal("PostgreSQL", hits[0].Title);
        Assert.Equal("https://www.wikidata.org/wiki/Q850", hits[1].Url);
    }

    [Fact]
    public void ParseSearchHits_RespectsLimitAndToleratesGarbage()
    {
        const string json = """{ "search": [ { "id": "Q1", "label": "A" }, { "id": "Q2", "label": "B" } ] }""";
        Assert.Single(WikidataSourceAdapter.ParseSearchHits(json, 1));
        Assert.Empty(WikidataSourceAdapter.ParseSearchHits("not json", 5));
        Assert.Empty(WikidataSourceAdapter.ParseSearchHits("{}", 5));
    }

    [Fact]
    public void ParseEntity_ExtractsLabelDescriptionAliasesAndTypedStatements()
    {
        var entity = WikidataSourceAdapter.ParseEntity(EntityJson, "Q1", 24);

        Assert.Equal("PostgreSQL", entity.Label);
        Assert.Equal("open-source relational database management system", entity.Description);
        Assert.Equal(new[] { "Postgres" }, entity.Aliases.ToArray());

        // novalue snak is skipped; typed datavalues are rendered to literals or entity refs.
        Assert.Equal(4, entity.Statements.Count);
        Assert.Contains(entity.Statements, s => s.PropertyId == "P31" && s.EntityValueId == "Q176165");
        Assert.Contains(entity.Statements, s => s.PropertyId == "P571" && s.LiteralValue == "1996-07-08"); // time formatted
        Assert.Contains(entity.Statements, s => s.PropertyId == "P277" && s.EntityValueId == "Q15777");
        Assert.Contains(entity.Statements, s => s.PropertyId == "P348" && s.LiteralValue == "16.1");
    }

    [Fact]
    public void ParseEntity_BoundsStatementCount()
    {
        var entity = WikidataSourceAdapter.ParseEntity(EntityJson, "Q1", 2);
        Assert.Equal(2, entity.Statements.Count);
    }

    [Fact]
    public void ParseEntity_MissingEntityYieldsEmpty()
    {
        var entity = WikidataSourceAdapter.ParseEntity(EntityJson, "Q999", 24);
        Assert.Equal(string.Empty, entity.Label);
        Assert.Empty(entity.Statements);
    }

    [Fact]
    public void CollectLabelIds_IncludesPropertyAndEntityValueIds()
    {
        var entity = WikidataSourceAdapter.ParseEntity(EntityJson, "Q1", 24);
        var ids = WikidataSourceAdapter.CollectLabelIds(entity);

        Assert.Contains("P31", ids);
        Assert.Contains("Q176165", ids);
        Assert.Contains("P277", ids);
        Assert.Contains("Q15777", ids);
        Assert.Contains("P348", ids);
        Assert.Equal(ids.Distinct().Count(), ids.Count); // de-duplicated
    }

    [Fact]
    public void ParseLabels_MapsIdsToEnglishLabels()
    {
        var labels = WikidataSourceAdapter.ParseLabels(LabelsJson);
        Assert.Equal("instance of", labels["P31"]);
        Assert.Equal("relational database management system", labels["Q176165"]);
        Assert.Equal("C", labels["Q15777"]);
    }

    [Fact]
    public void RenderText_ProducesReadableFactsUsingResolvedLabels()
    {
        var entity = WikidataSourceAdapter.ParseEntity(EntityJson, "Q1", 24);
        var labels = WikidataSourceAdapter.ParseLabels(LabelsJson);

        var text = WikidataSourceAdapter.RenderText(entity, labels, 24);

        Assert.Contains("PostgreSQL", text);
        Assert.Contains("open-source relational database management system", text);
        Assert.Contains("Also known as: Postgres", text);
        Assert.Contains("instance of: relational database management system", text);
        Assert.Contains("inception: 1996-07-08", text);
        Assert.Contains("programmed in: C", text);
        Assert.Contains("software version identifier: 16.1", text);
        // Unresolved ids never appear once labels are provided.
        Assert.DoesNotContain("P31:", text);
        Assert.DoesNotContain("Q176165", text);
    }

    [Fact]
    public void RenderText_FallsBackToRawIdsWhenLabelsMissing()
    {
        var entity = WikidataSourceAdapter.ParseEntity(EntityJson, "Q1", 24);
        var text = WikidataSourceAdapter.RenderText(entity, new Dictionary<string, string>(), 24);

        // Without a label map, the property id is shown rather than crashing or omitting the fact.
        Assert.Contains("P31: Q176165", text);
    }
}
