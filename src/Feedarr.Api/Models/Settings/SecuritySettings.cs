namespace Feedarr.Api.Models.Settings;

public sealed class SecuritySettings
{
    // "none" | "basic"
    public string Authentication { get; set; } = "none";

    // "local" | "all" (local = no auth for local addresses)
    public string AuthenticationRequired { get; set; } = "local";

    public string Username { get; set; } = "";

    // Stored as base64
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
}
