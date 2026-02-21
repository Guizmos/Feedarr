namespace Feedarr.Api.Dtos.Providers;

public sealed class ExternalProviderInstanceDto
{
    public string InstanceId { get; set; } = "";
    public string ProviderKey { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; }
    public string? BaseUrl { get; set; }
    public Dictionary<string, bool> AuthFlags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long CreatedAtTs { get; set; }
    public long UpdatedAtTs { get; set; }
}
