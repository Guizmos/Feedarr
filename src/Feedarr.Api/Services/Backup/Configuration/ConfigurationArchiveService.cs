using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Feedarr.Api.Data;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Services.Backup.Configuration;

// One authenticated format. The public header specifies version, KDF and cipher.
public sealed class ConfigurationArchiveService(Db db, IApiKeyProtectionService protection, ExternalProviderRegistry registry)
{
    internal const int Iterations = 600_000;
    internal const int MaxConfigurationBytes = 8 * 1024 * 1024;
    internal const int MaxArchiveBytes = MaxConfigurationBytes + 128;
    private static readonly byte[] Header = "FEEDARR-CONFIG-3\nPBKDF2-SHA256:600000\nAES-256-GCM\n"u8.ToArray();
    private static int PrefixLength => Header.Length + 28;

    public Task<byte[]> ExportAsync(string password, CancellationToken ct = default)
    {
        CheckPassword(password);
        using var c = db.Open();
        var model = new ConfigurationSnapshotReader(protection, registry).Read(c);
        ConfigurationSchema.Validate(model, registry);
        var plain = JsonSerializer.SerializeToUtf8Bytes(model, ConfigurationSchema.Json);
        byte[]? key = null;
        try
        {
            if (plain.Length > MaxConfigurationBytes) throw new ConfigurationArchiveException(ConfigurationArchiveError.SizeLimitExceeded);
            ct.ThrowIfCancellationRequested();
            var output = new byte[PrefixLength + plain.Length + 16];
            Header.CopyTo(output, 0);
            RandomNumberGenerator.Fill(output.AsSpan(Header.Length, 28));
            key = Derive(password, output.AsSpan(Header.Length, 16));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(Header.Length + 16, 12), plain, output.AsSpan(PrefixLength, plain.Length),
                output.AsSpan(PrefixLength + plain.Length, 16), output.AsSpan(0, PrefixLength));
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(output);
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    public async Task<ConfigurationArchiveReadResult> ReadAsync(Stream input, string password, CancellationToken ct = default)
    {
        CheckPassword(password);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        byte[]? plain = null, key = null;
        try
        {
            int n;
            while ((n = await input.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + n > MaxArchiveBytes) throw new ConfigurationArchiveException(ConfigurationArchiveError.SizeLimitExceeded);
                buffer.Write(chunk, 0, n);
            }
            var bytes = buffer.GetBuffer(); var length = (int)buffer.Length;
            if (length <= PrefixLength + 16 || !bytes.AsSpan(0, Header.Length).SequenceEqual(Header))
                throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidArchive);
            if (length - PrefixLength - 16 > MaxConfigurationBytes) throw new ConfigurationArchiveException(ConfigurationArchiveError.SizeLimitExceeded);
            plain = new byte[length - PrefixLength - 16];
            key = Derive(password, bytes.AsSpan(Header.Length, 16));
            using var aes = new AesGcm(key, 16);
            try { aes.Decrypt(bytes.AsSpan(Header.Length + 16, 12), bytes.AsSpan(PrefixLength, plain.Length), bytes.AsSpan(length - 16, 16), plain, bytes.AsSpan(0, PrefixLength)); }
            catch (CryptographicException) { throw new ConfigurationArchiveException(ConfigurationArchiveError.AuthenticationFailed); }
            ct.ThrowIfCancellationRequested();
            var model = JsonSerializer.Deserialize<ConfigurationDocument>(plain, ConfigurationSchema.Json)
                ?? throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidConfiguration);
            return new() { Configuration = model, Preview = ConfigurationSchema.Validate(model, registry) };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidConfiguration); }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
            if (buffer.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan());
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }
    private static byte[] Derive(string password, ReadOnlySpan<byte> salt) => Rfc2898DeriveBytes.Pbkdf2(password.AsSpan(), salt, Iterations, HashAlgorithmName.SHA256, 32);
    internal static void CheckPassword(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 12 or > 1024) throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidPassphrase);
    }
    internal static JsonObject ParseObject(string json) => JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 32 })?.AsObject()
        ?? throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidConfiguration);
}
