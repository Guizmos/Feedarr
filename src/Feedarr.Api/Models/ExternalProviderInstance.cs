namespace Feedarr.Api.Models;

public sealed class ExternalProviderInstance
{
    public string InstanceId { get; set; } = "";
    public string ProviderKey { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; }
    public string? BaseUrl { get; set; }
    public Dictionary<string, string?> Auth { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long CreatedAtTs { get; set; }
    public long UpdatedAtTs { get; set; }
}
