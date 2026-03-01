namespace Feedarr.Api.Models.Settings;

public sealed class SecuritySettings
{
    // "smart" | "strict" | "open"
    public string AuthMode { get; set; } = "smart";

    // "none" | "basic"
    public string Authentication { get; set; } = "none";

    // "local" | "all" (local = no auth for local addresses)
    public string AuthenticationRequired { get; set; } = "local";

    // Optional absolute URL used by Smart mode exposure detection.
    public string PublicBaseUrl { get; set; } = "";

    public string Username { get; set; } = "";

    // Stored as base64
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
}
