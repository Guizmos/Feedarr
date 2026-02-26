using System.ComponentModel.DataAnnotations;

namespace Feedarr.Api.Dtos.Providers;

public sealed class ExternalProviderCreateDto
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string ProviderKey { get; set; } = "";

    [StringLength(200)]
    public string? DisplayName { get; set; }

    public bool? Enabled { get; set; }

    [StringLength(1000)]
    public string? BaseUrl { get; set; }

    public Dictionary<string, string?>? Auth { get; set; }
    public Dictionary<string, object?>? Options { get; set; }
}
