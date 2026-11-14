using System.Data;
using Dapper;
using Feedarr.Api.Services.Matching;
using Microsoft.Extensions.Logging;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    private sealed class RssIdentityRow
    {
        public long Id { get; set; }
        public string Guid { get; set; } = "";
        public string? Title { get; set; }
        public string? TitleClean { get; set; }
        public int? Year { get; set; }
        public string? MediaType { get; set; }
        public bool ManualTitleOverride { get; set; }
        public string? ManualMatchProvider { get; set; }
    }

    // Compare work identity, not raw formatting or encoding tags. Losing a year/type
    // alone is not evidence of another work; a new explicit year/type can disambiguate it.
    // This runs inside the RSS transaction and never mutates the old shared entity.
    private void PrepareRssIdentityChanges(IDbConnection conn, IDbTransaction tx,
        IReadOnlyList<UpsertRow> rows, Dictionary<string, RssIdentityRow> identities)
    {
        var resetIds = new HashSet<long>();
        foreach (var row in rows)
        {
            if (!identities.TryGetValue(row.Guid, out var old))
            {
                identities[row.Guid] = new RssIdentityRow
                { Guid = row.Guid, Title = row.Title, TitleClean = row.TitleClean, Year = row.Year, MediaType = row.MediaType };
                continue;
            }
            if (old.ManualTitleOverride) continue; // Explicit user title always wins.
            var changed = !string.Equals(old.Title, row.Title, StringComparison.Ordinal) &&
                (TitleNormalizer.NormalizeTitleStrict(old.TitleClean) != row.TitleNormalized ||
                (row.Year.HasValue && row.Year != old.Year) ||
                (old.ManualMatchProvider is null && !string.IsNullOrWhiteSpace(row.MediaType) &&
                 !string.Equals(row.MediaType, old.MediaType, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrEmpty(old.ManualMatchProvider))
            {
                row.PreserveIdentity = true;
                if (changed) _logger.LogWarning("rss_identity_conflict sourceId={SourceId} releaseId={ReleaseId}: RSS work changed; explicit manual association preserved", row.sourceId, old.Id);
                continue;
            }
            row.IdentityChanged = changed;
            if (changed && old.Id > 0) resetIds.Add(old.Id);
            // Track sequential duplicates too, matching the prepared upsert's last-write order.
            old.Title = row.Title;
            old.TitleClean = row.TitleClean ?? old.TitleClean;
            old.Year = changed ? row.Year : row.Year ?? old.Year;
            old.MediaType = changed ? row.MediaType : row.MediaType ?? old.MediaType;
        }
        foreach (var chunk in SqlChunkHelper.Chunk(resetIds.ToList(), 500))
            conn.Execute("DELETE FROM release_arr_status WHERE release_id IN @ids", new { ids = chunk }, tx);
    }
}
