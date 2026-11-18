using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// Model-free: arXiv Atom-XML parsing and rendering, against a canned feed (no network).
// The live search/fetch is covered by the gated Integration tests.
public sealed class ArxivSourceAdapterTests
{
    private const string Feed = """
    <?xml version="1.0" encoding="UTF-8"?>
    <feed xmlns="http://www.w3.org/2005/Atom">
      <title>ArXiv Query</title>
      <entry>
        <id>http://arxiv.org/abs/1706.03762v7</id>
        <title>Attention Is All You Need</title>
        <summary>
          The dominant sequence transduction models are based on complex recurrent
          or convolutional neural networks.
        </summary>
        <author><name>Ashish Vaswani</name></author>
        <author><name>Noam Shazeer</name></author>
      </entry>
      <entry>
        <id>http://arxiv.org/abs/1810.04805v2</id>
        <title>BERT</title>
        <summary>We introduce a new language representation model called BERT.</summary>
        <author><name>Jacob Devlin</name></author>
      </entry>
    </feed>
    """;

    [Fact]
    public void ParseSearchHits_MapsEntriesToExportArxivHandles()
    {
        var hits = ArxivSourceAdapter.ParseSearchHits(Feed, 5);

        Assert.Equal(2, hits.Count);
        Assert.Equal("https://export.arxiv.org/abs/1706.03762v7", hits[0].Url);
        Assert.Equal("Attention Is All You Need", hits[0].Title);
        Assert.Equal("https://export.arxiv.org/abs/1810.04805v2", hits[1].Url);
    }

    [Fact]
    public void ParseSearchHits_RespectsLimitAndToleratesGarbage()
    {
        Assert.Single(ArxivSourceAdapter.ParseSearchHits(Feed, 1));
        Assert.Empty(ArxivSourceAdapter.ParseSearchHits("not xml", 5));
        Assert.Empty(ArxivSourceAdapter.ParseSearchHits(
            """<feed xmlns="http://www.w3.org/2005/Atom"></feed>""", 5));
    }

    [Theory]
    [InlineData("http://arxiv.org/abs/1706.03762v7", "1706.03762v7")]
    [InlineData("http://arxiv.org/abs/math/0309136", "math/0309136")]
    [InlineData("no-marker-here", "")]
    public void ArxivIdFromAbsUrl_ExtractsTheIdentifier(string absUrl, string expected)
    {
        Assert.Equal(expected, ArxivSourceAdapter.ArxivIdFromAbsUrl(absUrl));
    }

    [Fact]
    public void ParsePaper_ExtractsIdTitleAuthorsAndCollapsedAbstract()
    {
        var paper = ArxivSourceAdapter.ParsePaper(Feed); // first entry

        Assert.NotNull(paper);
        Assert.Equal("1706.03762v7", paper!.Id);
        Assert.Equal("Attention Is All You Need", paper.Title);
        Assert.Equal(new[] { "Ashish Vaswani", "Noam Shazeer" }, paper.Authors.ToArray());
        // Abstract whitespace/newlines collapsed to single spaces.
        Assert.Equal(
            "The dominant sequence transduction models are based on complex recurrent or convolutional neural networks.",
            paper.Abstract);
    }

    [Fact]
    public void ParsePaper_ReturnsNullForNoEntryOrGarbage()
    {
        Assert.Null(ArxivSourceAdapter.ParsePaper("""<feed xmlns="http://www.w3.org/2005/Atom"></feed>"""));
        Assert.Null(ArxivSourceAdapter.ParsePaper("not xml"));
    }

    [Fact]
    public void RenderPaperText_ProducesReadableTitleAuthorsAbstract()
    {
        var paper = ArxivSourceAdapter.ParsePaper(Feed);
        var text = ArxivSourceAdapter.RenderPaperText(paper!);

        Assert.Contains("Attention Is All You Need", text);
        Assert.Contains("Authors: Ashish Vaswani, Noam Shazeer", text);
        Assert.Contains("Abstract: The dominant sequence transduction models", text);
    }
}
