namespace Feedarr.Api.Services.Matching;

/// <summary>TMDB numbers are unique only within a media namespace.</summary>
public readonly record struct TmdbIdentity(string MediaType, int Id)
{
    public string Key => $"tmdb:{MediaType}:{Id}";
    public string ProviderId => $"{MediaType}:{Id}";
    public static string? NormalizeType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" or "film" => "movie",
        "tv" or "series" or "show" => "tv",
        _ => null
    };
}
