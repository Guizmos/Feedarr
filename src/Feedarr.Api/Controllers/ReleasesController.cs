using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Igdb;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/releases")]
public sealed class ReleasesController : ControllerBase
{
    private readonly ReleaseRepository _releases;
    private readonly Db _db;
    private readonly TitleParser _parser;
    private readonly IgdbClient _igdb;

    public ReleasesController(ReleaseRepository releases, Db db, TitleParser parser, IgdbClient igdb)
    {
        _releases = releases;
        _db = db;
        _parser = parser;
        _igdb = igdb;
    }

    // GET /api/releases/{id}/download
    [HttpGet("{id:long}/download")]
    public IActionResult Download([FromRoute] long id)
    {
        var url = _releases.GetDownloadUrl(id);

        if (string.IsNullOrWhiteSpace(url))
            return NotFound(new { error = "release not found or no download_url" });
        if (!OutboundUrlGuard.TryNormalizeDownloadUrl(url, out var normalizedUrl, out _))
            return BadRequest(new { error = "invalid download_url" });

        // 🔐 le front ne voit jamais jackett_apikey
        return Redirect(normalizedUrl);
    }

    // POST /api/releases/{id}/seen
    [HttpPost("{id:long}/seen")]
    public IActionResult MarkSeen([FromRoute] long id)
    {
        using var conn = _db.Open();
        var rows = conn.Execute("UPDATE releases SET seen = 1 WHERE id = @id", new { id });

        if (rows == 0) return NotFound(new { error = "release not found" });

        return NoContent(); // 204
    }

    // POST /api/releases/{id}/unseen
    [HttpPost("{id:long}/unseen")]
    public IActionResult MarkUnseen([FromRoute] long id)
    {
        using var conn = _db.Open();
        var rows = conn.Execute("UPDATE releases SET seen = 0 WHERE id = @id", new { id });

        if (rows == 0) return NotFound(new { error = "release not found" });

        return NoContent(); // 204
    }

    // POST /api/releases/seen  { "ids":[1,2,3], "seen": true }
    public sealed class BulkSeenDto
    {
        public List<long> Ids { get; set; } = new();
        public bool Seen { get; set; } = true;
    }

    [HttpPost("seen")]
    public IActionResult BulkSeen([FromBody] BulkSeenDto dto)
    {
        if (dto?.Ids == null || dto.Ids.Count == 0)
            return BadRequest(new { error = "ids missing" });

        using var conn = _db.Open();
        var rows = conn.Execute(
            "UPDATE releases SET seen = @seen WHERE id IN @ids",
            new { seen = dto.Seen ? 1 : 0, ids = dto.Ids.Distinct().ToArray() }
        );

        return Ok(new { ok = true, updated = rows });
    }

    public sealed class UpdateTitleDto
    {
        public string? Title { get; set; }
    }

    // PUT /api/releases/{id}/title  { "title": "..." }
    [HttpPost("{id:long}/title")]
    [HttpPut("{id:long}/title")]
    public IActionResult UpdateTitle([FromRoute] long id, [FromBody] UpdateTitleDto dto)
    {
        var title = (dto?.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { error = "title missing" });

        using var conn = _db.Open();
        var unifiedValue = conn.ExecuteScalar<string?>(
            "SELECT unified_category FROM releases WHERE id = @id",
            new { id });
        UnifiedCategoryMappings.TryParse(unifiedValue, out var unifiedCategory);

        var parsed = _parser.Parse(title, unifiedCategory);
        var rows = conn.Execute(
            """
            UPDATE releases
            SET title = @title,
                title_clean = @titleClean,
                year = @year,
                season = @season,
                episode = @episode,
                resolution = @resolution,
                source = @source,
                codec = @codec,
                release_group = @releaseGroup,
                media_type = @mediaType
            WHERE id = @id;
            """,
            new
            {
                id,
                title,
                titleClean = parsed.TitleClean,
                year = parsed.Year,
                season = parsed.Season,
                episode = parsed.Episode,
                resolution = parsed.Resolution,
                source = parsed.Source,
                codec = parsed.Codec,
                releaseGroup = parsed.ReleaseGroup,
                mediaType = parsed.MediaType
            }
        );

        if (rows == 0) return NotFound(new { error = "release not found" });

        return Ok(new
        {
            id,
            title,
            titleClean = parsed.TitleClean,
            year = parsed.Year,
            season = parsed.Season,
            episode = parsed.Episode,
            resolution = parsed.Resolution,
            source = parsed.Source,
            codec = parsed.Codec,
            releaseGroup = parsed.ReleaseGroup,
            mediaType = parsed.MediaType,
            unifiedCategory = unifiedCategory.ToString()
        });
    }

    // POST /api/releases/{id}/rename  { "title": "..." }
    [HttpPost("{id:long}/rename")]
    public IActionResult Rename([FromRoute] long id, [FromBody] UpdateTitleDto dto)
    {
        var title = (dto?.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { error = "title missing" });

        var row = _releases.RenameAndRebindEntity(id, title) as ReleaseRepository.RenameRebindResult;
        if (row is null) return NotFound(new { error = "release not found" });

        var entityId = row.EntityId;
        var posterUpdatedAtTs = row.PosterUpdatedAtTs ?? 0;
        string? posterUrl = null;

        if (entityId.HasValue && entityId.Value > 0 && !string.IsNullOrWhiteSpace(row.EntityPosterFile))
        {
            posterUrl = $"/api/posters/entity/{entityId.Value}?v={posterUpdatedAtTs}";
        }
        else if (!string.IsNullOrWhiteSpace(row.PosterFile))
        {
            posterUrl = $"/api/posters/release/{row.ReleaseId}?v={posterUpdatedAtTs}";
        }

        return Ok(new
        {
            releaseId = row.ReleaseId,
            entityId,
            title = row.Title,
            titleClean = row.TitleClean,
            year = row.Year,
            season = row.Season,
            episode = row.Episode,
            resolution = row.Resolution,
            source = row.Source,
            codec = row.Codec,
            releaseGroup = row.ReleaseGroup,
            mediaType = row.MediaType,
            unifiedCategory = row.UnifiedCategory,
            posterFile = row.PosterFile,
            posterUpdatedAtTs,
            posterUrl,
            tmdbId = row.TmdbId,
            tvdbId = row.TvdbId
        });
    }

    // POST /api/releases/{id}/details/igdb
    [HttpPost("{id:long}/details/igdb")]
    public async Task<IActionResult> FetchIgdbDetails([FromRoute] long id, [FromQuery] bool? force, CancellationToken ct)
    {
        var r = _releases.GetForPoster(id);
        if (r is null) return NotFound(new { error = "release not found" });

        var unifiedValue = (string?)r.UnifiedCategory;
        UnifiedCategoryMappings.TryParse(unifiedValue, out var unifiedCategory);
        var isGame = unifiedCategory == UnifiedCategory.JeuWindows;
        if (!isGame && force != true)
            return BadRequest(new { error = "release is not a game" });

        var extOverview = (string?)r.ExtOverview;
        var extReleaseDate = (string?)r.ExtReleaseDate;
        var extGenres = (string?)r.ExtGenres;
        var extRating = r.ExtRating is null ? (double?)null : Convert.ToDouble(r.ExtRating);
        var extVotes = r.ExtVotes is null ? (int?)null : Convert.ToInt32(r.ExtVotes);
        var extProvider = (string?)r.ExtProvider;
        var extProviderId = (string?)r.ExtProviderId;

        var hasDetails =
            !string.IsNullOrWhiteSpace(extOverview) ||
            !string.IsNullOrWhiteSpace(extReleaseDate) ||
            !string.IsNullOrWhiteSpace(extGenres) ||
            (extRating is not null && extRating > 0) ||
            (extVotes is not null && extVotes > 0);

        if (hasDetails && force != true)
        {
            return Ok(new
            {
                ok = true,
                cached = true,
                details = new
                {
                    overview = extOverview,
                    releaseDate = extReleaseDate,
                    genres = extGenres,
                    rating = extRating,
                    ratingVotes = extVotes,
                    detailsProvider = extProvider,
                    detailsProviderId = extProviderId
                }
            });
        }

        int? igdbId = null;
        if (string.Equals(extProvider, "igdb", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(extProviderId, out var extIgdbId))
        {
            igdbId = extIgdbId;
        }

        var posterProvider = (string?)r.PosterProvider;
        var posterProviderId = (string?)r.PosterProviderId;
        if (!igdbId.HasValue &&
            string.Equals(posterProvider, "igdb", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(posterProviderId, out var posterIgdbId))
        {
            igdbId = posterIgdbId;
        }

        if (!igdbId.HasValue)
        {
            var title = (string?)r.TitleClean ?? (string?)r.Title ?? "";
            var year = r.Year is null ? (int?)null : Convert.ToInt32(r.Year);
            if (string.IsNullOrWhiteSpace(title))
                return BadRequest(new { error = "title missing" });

            var match = await _igdb.SearchGameCoverAsync(title, year, ct);
            if (match is null)
                return NotFound(new { error = "igdb match not found" });

            igdbId = match.Value.igdbId;
        }

        var details = await _igdb.GetGameDetailsAsync(igdbId.Value, ct);
        if (details is null)
            return StatusCode(502, new { error = "igdb details not available" });

        _releases.UpdateExternalDetails(
            id,
            "igdb",
            igdbId.Value.ToString(CultureInfo.InvariantCulture),
            details.Title,
            details.Summary,
            null,
            details.Genres,
            details.ReleaseDate,
            null,
            details.Rating,
            details.Votes,
            null,
            null,
            null
        );

        return Ok(new
        {
            ok = true,
            details = new
            {
                overview = details.Summary,
                releaseDate = details.ReleaseDate,
                genres = details.Genres,
                rating = details.Rating,
                ratingVotes = details.Votes,
                detailsProvider = "igdb",
                detailsProviderId = igdbId.Value.ToString(CultureInfo.InvariantCulture)
            }
        });
    }

}
