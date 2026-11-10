using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Feedarr.Api.Data;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Security;
using Microsoft.Data.Sqlite;

namespace Feedarr.Api.Services.Backup.Configuration;

/// <summary>
/// Configuration-only export/read engine. No restore, active database writes,
/// temporary plaintext files, HTTP responses or secret logging. Caller owns the
/// passphrase and the sensitive in-memory document returned by ReadAsync.
/// </summary>
public sealed class ConfigurationArchiveService(
    Db db, IApiKeyProtectionService protection, ExternalProviderRegistry registry,
    BackupValidationService legacyValidation)
{
    internal const int Iterations = 600_000;
    internal const int MaxConfigurationBytes = 8 * 1024 * 1024;
    internal const int MaxArchiveBytes = 16 * 1024 * 1024;
    internal const int MaxLegacyBytes = 64 * 1024 * 1024;
    private const int MaxManifestBytes = 4096;
    private const string Format = "Feedarr.Configuration";
    private const int FormatVersion = 2;

    private sealed class Manifest
    {
        public required string Format { get; init; }
        public required int FormatVersion { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Kdf { get; init; }
        public required int Iterations { get; init; }
        public required string Cipher { get; init; }
        public required string Salt { get; init; }
        public required string Nonce { get; init; }
    }

    public Task<byte[]> ExportAsync(string passphrase, CancellationToken ct = default)
    {
        CheckPassphrase(passphrase);
        ct.ThrowIfCancellationRequested();
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = db.DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            SetReadOnly(connection, ct);
            var model = new ConfigurationSnapshotReader(protection, registry).Read(connection, false, out _);
            ConfigurationSchema.Validate(model, registry);
            return Task.FromResult(Encrypt(model, passphrase, ct));
        }
        catch (ConfigurationArchiveException) { throw; }
        catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException or FormatException or ArgumentException)
        { throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidConfiguration); }
    }

    public async Task<ConfigurationArchiveReadResult> ReadAsync(Stream input, string passphrase, CancellationToken ct = default)
    {
        byte[]? bytes = null;
        try
        {
            bytes = await ReadBoundedAsync(input, MaxLegacyBytes, ct).ConfigureAwait(false);
            ValidateZipEnvelope(bytes);
            using var memory = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
            if (archive.Entries.Count is < 2 or > 3) Invalid();
            if (archive.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count) Invalid();
            if (archive.GetEntry("manifest.json") is not null)
            {
                if (bytes.Length > MaxArchiveBytes) TooLarge();
                CheckPassphrase(passphrase);
                if (archive.Entries.Count != 2 || archive.GetEntry("configuration.enc") is null) Invalid();
                var manifestBytes = await ReadEntry(archive.GetEntry("manifest.json")!, MaxManifestBytes, ct).ConfigureAwait(false);
                var manifest = Deserialize<Manifest>(manifestBytes);
                if (manifest.Format != Format) Invalid();
                if (manifest.FormatVersion != FormatVersion || manifest.SchemaVersion != ConfigurationSchema.Version)
                    throw new ConfigurationArchiveException(ConfigurationArchiveError.UnsupportedVersion);
                if (manifest.Kdf != "PBKDF2-HMAC-SHA256" || manifest.Cipher != "AES-256-GCM" ||
                    manifest.Iterations is < Iterations or > 1_200_000) Invalid();
                var salt = Convert.FromBase64String(manifest.Salt);
                var nonce = Convert.FromBase64String(manifest.Nonce);
                if (salt.Length != 16 || nonce.Length != 12) Invalid();
                var encrypted = await ReadEntry(archive.GetEntry("configuration.enc")!, MaxConfigurationBytes + 16, ct).ConfigureAwait(false);
                if (encrypted.Length <= 16) Invalid();
                var key = Derive(passphrase, salt, manifest.Iterations);
                var plaintext = new byte[encrypted.Length - 16];
                try
                {
                    ct.ThrowIfCancellationRequested();
                    using var aes = new AesGcm(key, 16);
                    try { aes.Decrypt(nonce, encrypted.AsSpan(0, plaintext.Length), encrypted.AsSpan(plaintext.Length), plaintext, manifestBytes); }
                    catch (CryptographicException) { throw new ConfigurationArchiveException(ConfigurationArchiveError.AuthenticationFailed); }
                    var model = Deserialize<ConfigurationDocument>(plaintext);
                    var preview = ConfigurationSchema.Validate(model, registry);
                    return new() { Configuration = model, Preview = preview, IsLegacy = false, SecretsToReenter = ConfigurationSchema.MissingRequiredSecrets(model, registry) };
                }
                finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
            }
            return await ReadLegacy(archive, ct).ConfigureAwait(false);
        }
        catch (ConfigurationArchiveException) { throw; }
        catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or InvalidOperationException or ArgumentException or SqliteException or IOException)
        { throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidArchive); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Encrypt(ConfigurationDocument model, string passphrase, CancellationToken ct)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(model, ConfigurationSchema.Json);
        byte[]? key = null;
        try
        {
            if (plaintext.Length > MaxConfigurationBytes) TooLarge();
            var salt = RandomNumberGenerator.GetBytes(16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new Manifest
            {
                Format = Format,
                FormatVersion = FormatVersion,
                SchemaVersion = ConfigurationSchema.Version,
                Kdf = "PBKDF2-HMAC-SHA256",
                Iterations = Iterations,
                Cipher = "AES-256-GCM",
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce)
            }, ConfigurationSchema.Json);
            key = Derive(passphrase, salt, Iterations);
            ct.ThrowIfCancellationRequested();
            var encrypted = new byte[plaintext.Length + 16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plaintext, encrypted.AsSpan(0, plaintext.Length), encrypted.AsSpan(plaintext.Length), manifest);
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open()) entry.Write(manifest);
                using (var entry = zip.CreateEntry("configuration.enc", CompressionLevel.NoCompression).Open()) entry.Write(encrypted);
            }
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private async Task<ConfigurationArchiveReadResult> ReadLegacy(ZipArchive archive, CancellationToken ct)
    {
        var currentVersion = typeof(ConfigurationArchiveService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        if (!legacyValidation.TryValidateArchiveForRestore(archive, "feedarr.db", currentVersion, out var name, out var details, out _)) Invalid();
        // Old config.json is redacted and is not a source of truth. No other entries are accepted.
        if (archive.Entries.Any(e => e.FullName != name && e.FullName != "info.json" && e.FullName != "config.json")) Invalid();
        var data = await ReadEntry(archive.GetEntry(name)!, MaxLegacyBytes, ct).ConfigureAwait(false);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), Convert.FromHexString(details.DbSha256))) Invalid();
            if (data.Length < 100 || !data.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8)) Invalid();
            // A legacy archive is a standalone snapshot, never a live WAL database.
            // Deserialize cannot open WAL mode; normalize only the private memory image.
            data[18] = 1; data[19] = 1;
            var buffer = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, buffer, data.Length);
                using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
                connection.Open();
                const int deserializeReadOnly = 4;
                if (SQLitePCL.raw.sqlite3_deserialize(connection.Handle, "main", buffer, data.Length, data.Length, deserializeReadOnly) != 0) Invalid();
                SetReadOnly(connection, ct);
                var model = new ConfigurationSnapshotReader(protection, registry).Read(connection, true, out var unreadable);
                var preview = ConfigurationSchema.Validate(model, registry);
                // Include unreadable optional/disabled secrets too, never claim a complete recovery.
                var gaps = ConfigurationSchema.MissingRequiredSecrets(model, registry).Concat(unreadable).Distinct().ToArray();
                if (gaps.Length > 0) preview = preview with { MissingSecrets = gaps.Length, IsComplete = false };
                return new() { Configuration = model, Preview = preview, IsLegacy = true, SecretsToReenter = gaps };
            }
            finally
            {
                var zeros = new byte[8192];
                for (var offset = 0; offset < data.Length; offset += zeros.Length)
                    Marshal.Copy(zeros, 0, IntPtr.Add(buffer, offset), Math.Min(zeros.Length, data.Length - offset));
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private static void ValidateZipEnvelope(byte[] bytes)
    {
        // Check entry count before ZipArchive materializes its entry list. Our
        // bounded formats need neither ZIP64, multi-disk archives nor comments.
        if (bytes.Length < 22) Invalid();
        var end = bytes.AsSpan(bytes.Length - 22);
        if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054b50 ||
            BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != 0) Invalid();
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        if (entries is < 2 or > 3 || BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != entries) Invalid();
        var size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        if (size > 64 * 1024 || (long)offset + size != bytes.Length - 22) Invalid();
    }

    private static void SetReadOnly(SqliteConnection connection, CancellationToken ct)
    {
        SQLitePCL.raw.sqlite3_limit(connection.Handle, 0, MaxConfigurationBytes); // value/blob length
        SQLitePCL.raw.sqlite3_limit(connection.Handle, 1, 1_048_576); // schema SQL length
        SQLitePCL.raw.sqlite3_limit(connection.Handle, 2, 256); // columns
        SQLitePCL.raw.sqlite3_limit(connection.Handle, 7, 0); // attached databases
        var started = System.Diagnostics.Stopwatch.StartNew();
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, _ =>
            ct.IsCancellationRequested || started.Elapsed > TimeSpan.FromSeconds(15) ? 1 : 0, null);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;";
        command.ExecuteNonQuery();
    }
    private static byte[] Derive(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase.AsSpan(), salt, iterations, HashAlgorithmName.SHA256, 32);
    private static void CheckPassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length is < 12 or > 1024)
            throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidPassphrase);
    }
    private static async Task<byte[]> ReadEntry(ZipArchiveEntry entry, int limit, CancellationToken ct)
    {
        if (entry.Length <= 0) Invalid();
        if (entry.Length > limit) TooLarge();
        using var stream = entry.Open();
        return await ReadBoundedAsync(stream, limit, ct).ConfigureAwait(false);
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > limit) TooLarge();
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var written)) CryptographicOperations.ZeroMemory(written.AsSpan());
        }
    }
    internal static JsonObject ParseObject(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        try { return Deserialize<JsonObject>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static T Deserialize<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        CheckDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, ConfigurationSchema.Json)
            ?? throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidArchive);
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!keys.Add(property.Name)) Invalid(); CheckDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }
    private static void Invalid() => throw new ConfigurationArchiveException(ConfigurationArchiveError.InvalidArchive);
    private static void TooLarge() => throw new ConfigurationArchiveException(ConfigurationArchiveError.SizeLimitExceeded);
}
