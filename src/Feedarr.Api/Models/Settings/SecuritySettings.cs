namespace Feedarr.Api.Models.Settings;

public sealed class SecuritySettings
{
    public bool RequireHttps { get; set; } = false;
    public string TransportMigration { get; set; } = "";

    // Legacy archive fields only. They never control authentication.
    public string AuthMode { get; set; } = "";

    // "none" | "basic"
    public string Authentication { get; set; } = "";

    // "local" | "all" (local = no auth for local addresses)
    public string AuthenticationRequired { get; set; } = "";

    // Optional public URL retained for existing configuration and archives.
    public string PublicBaseUrl { get; set; } = "";

    public string Username { get; set; } = "";

    // Stored as base64
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";

    public static SecuritySettings BuildDefaults() => new();
}
