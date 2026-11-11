namespace Feedarr.Api.Models;

public sealed record PosterMatchIds(
    int? TmdbId,
    int? TvdbId,
    int? TvmazeId,
    int? IgdbId,
    string? ImdbId,
    string? TmdbMediaType = null)
{
    public bool HasAny =>
        TmdbId.HasValue || TvdbId.HasValue || TvmazeId.HasValue || IgdbId.HasValue || !string.IsNullOrWhiteSpace(ImdbId);

    public bool Overlaps(PosterMatchIds other)
    {
        if (other is null) return false;
        if (TmdbId.HasValue && other.TmdbId == TmdbId &&
            Services.Matching.TmdbIdentity.NormalizeType(TmdbMediaType) is { } type &&
            type == Services.Matching.TmdbIdentity.NormalizeType(other.TmdbMediaType)) return true;
        if (TvdbId.HasValue && other.TvdbId == TvdbId) return true;
        if (TvmazeId.HasValue && other.TvmazeId == TvmazeId) return true;
        if (IgdbId.HasValue && other.IgdbId == IgdbId) return true;
        if (!string.IsNullOrWhiteSpace(ImdbId) && string.Equals(ImdbId, other.ImdbId, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
