using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Feedarr.Api.Services.Backup.Configuration;

/// <summary>
/// Portable schema, independent of SQLite row IDs. Category/profile/tag IDs are
/// remote integration identifiers. Secrets exist in memory only until encrypted;
/// never return this document directly from an HTTP endpoint or log its contents.
/// </summary>
public sealed class ConfigurationDocument
{
    public required int SchemaVersion { get; init; }
    public required Dictionary<string, JsonObject> Settings { get; init; }
    public required List<JsonObject> Providers { get; init; }
    public required List<JsonObject> Sources { get; init; }
    public required List<JsonObject> ExternalProviders { get; init; }
    public required List<JsonObject> Applications { get; init; }
    public required List<JsonObject> Categories { get; init; }
    public required List<JsonObject> Mappings { get; init; }
    public required List<JsonObject> Selections { get; init; }
}

public sealed record ConfigurationPreview(int Sources, int IndexerProviders, int ExternalProviders, int Applications, int Settings, int MissingSecrets);

public sealed class ConfigurationArchiveReadResult
{
    // Safe default for future API serialization: only the aggregate preview is exposed.
    [JsonIgnore] public ConfigurationDocument Configuration { get; init; } = null!;
    public required ConfigurationPreview Preview { get; init; }
}

public sealed record ConfigurationSecretRequirement(string ItemKey, string Field);

public enum ConfigurationArchiveError
{
    InvalidArchive, UnsupportedVersion, InvalidConfiguration, InvalidPassphrase,
    AuthenticationFailed, UnreadableSecrets, SizeLimitExceeded
}

public sealed class ConfigurationArchiveException : Exception
{
    public ConfigurationArchiveError Code { get; }
    public ConfigurationArchiveException(ConfigurationArchiveError code) : base(code switch
    {
        ConfigurationArchiveError.UnsupportedVersion => "Unsupported configuration archive version.",
        ConfigurationArchiveError.InvalidConfiguration => "The configuration structure or relationships are invalid.",
        ConfigurationArchiveError.InvalidPassphrase => "A passphrase of 12 to 1024 characters is required.",
        ConfigurationArchiveError.AuthenticationFailed => "Incorrect passphrase or damaged configuration archive.",
        ConfigurationArchiveError.UnreadableSecrets => "Some local secrets cannot be decrypted; no archive was created.",
        ConfigurationArchiveError.SizeLimitExceeded => "The configuration archive exceeds its safety limits.",
        _ => "Invalid configuration archive."
    }) => Code = code;
}
