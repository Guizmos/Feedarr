namespace Feedarr.Api.Models;

/// <summary>
/// Représente un match de poster trouvé pour une release existante.
/// </summary>
public sealed class PosterMatch
{
    public long Id { get; set; }
    public long? EntityId { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public string? PosterPath { get; set; }
    public string? PosterFile { get; set; }
    public string? PosterProvider { get; set; }
    public string? PosterProviderId { get; set; }
    public string? PosterLang { get; set; }
    public string? PosterSize { get; set; }
    public string? PosterHash { get; set; }

    // Détails externes
    public string? ExtProvider { get; set; }
    public string? ExtProviderId { get; set; }
    public string? ExtTitle { get; set; }
    public string? ExtOverview { get; set; }
    public string? ExtTagline { get; set; }
    public string? ExtGenres { get; set; }
    public string? ExtReleaseDate { get; set; }
    public int? ExtRuntimeMinutes { get; set; }
    public double? ExtRating { get; set; }
    public int? ExtVotes { get; set; }
    public long? ExtUpdatedAtTs { get; set; }
    public string? ExtDirectors { get; set; }
    public string? ExtWriters { get; set; }
    public string? ExtCast { get; set; }
}
