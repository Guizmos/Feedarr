namespace Feedarr.Api.Models.Settings;

public sealed class ExternalSettings
{
    public string? TmdbApiKey { get; set; }
    public string? TvmazeApiKey { get; set; }
    public string? FanartApiKey { get; set; }
    public string? IgdbClientId { get; set; }
    public string? IgdbClientSecret { get; set; }

    public bool? TmdbEnabled { get; set; }
    public bool? TvmazeEnabled { get; set; }
    public bool? FanartEnabled { get; set; }
    public bool? IgdbEnabled { get; set; }
}
