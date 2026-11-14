namespace Feedarr.Api.Services.Tmdb;

public sealed partial class TmdbClient
{
    internal sealed record ObservedSearchResult(SearchResult Result, string? ResponseLanguage);
}
