namespace Feedarr.Api.Models;

/// <summary>
/// Représente une release avec toutes les infos nécessaires pour le fetch de poster.
/// </summary>
public sealed class ReleaseForPoster
{
    public long Id { get; set; }
    public long SourceId { get; set; }
    public long? EntityId { get; set; }
    public string? Title { get; set; }
    public string? TitleClean { get; set; }
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public int? StdCategoryId { get; set; }
    public int? SpecCategoryId { get; set; }
    public string? UnifiedCategory { get; set; }
    public string? CategoryIds { get; set; }
    public string? MediaType { get; set; }

    // IDs externes
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }

    // Infos poster
    public string? PosterPath { get; set; }
    public string? PosterFile { get; set; }
    public string? PosterProvider { get; set; }
    public string? PosterProviderId { get; set; }
    public string? PosterLang { get; set; }
    public string? PosterSize { get; set; }
    public string? PosterHash { get; set; }
    public long? PosterUpdatedAtTs { get; set; }
    public long? PosterLastAttemptTs { get; set; }
    public string? PosterLastError { get; set; }

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
