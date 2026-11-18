using System.Security.Cryptography;
using System.Text;
using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;

namespace ResearchMissionControl.Infrastructure;

public sealed record AcquiredEvidence(string SnapshotId, IReadOnlyList<string> PassageIds);

// Host-governed evidence acquisition. A fetch runs only when the host authorizes the
// tool proposal; the result becomes an immutable snapshot with bounded passages.
public sealed class EvidenceAcquisition
{
    private const int PassageMaxChars = 1200;

    private readonly ISourceAdapter _source;
    private readonly EvidenceStore _store;
    private readonly ToolRegistry _tools;
    private readonly Func<string> _idFactory;
    private readonly Func<long> _clock;

    public EvidenceAcquisition(
        ISourceAdapter source,
        EvidenceStore store,
        ToolRegistry tools,
        Func<string>? idFactory = null,
        Func<long>? clock = null)
    {
        _source = source;
        _store = store;
        _tools = tools;
        _idFactory = idFactory ?? (() => Guid.NewGuid().ToString("N"));
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // Returns null (and performs NO fetch) when the tool proposal is not authorized.
    public async Task<AcquiredEvidence?> FetchAsync(
        string jobId,
        ToolProposal proposal,
        string url,
        CancellationToken cancellationToken)
    {
        var authorization = _tools.Authorize(proposal);
        if (!authorization.Authorized)
            return null;

        var document = await _source.FetchAsync(url, cancellationToken).ConfigureAwait(false);
        var snapshotId = _idFactory();
        _store.SaveSnapshot(
            snapshotId, jobId, document.SourceIdentity, document.Url, _clock(),
            ComputeDigest(document.Text), document.Rights, "fetched", "ok");

        var passageIds = new List<string>();
        var chunks = Chunk(document.Text, PassageMaxChars);
        for (var i = 0; i < chunks.Count; i++)
        {
            var passageId = _idFactory();
            _store.AddPassage(passageId, snapshotId, i, chunks[i]);
            passageIds.Add(passageId);
        }
        return new AcquiredEvidence(snapshotId, passageIds);
    }

    private static List<string> Chunk(string text, int size)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text))
            return chunks;
        for (var i = 0; i < text.Length; i += size)
            chunks.Add(text.Substring(i, Math.Min(size, text.Length - i)));
        return chunks;
    }

    private static string ComputeDigest(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
