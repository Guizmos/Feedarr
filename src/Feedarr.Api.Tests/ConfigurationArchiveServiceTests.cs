using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Backup.Configuration;
using Feedarr.Api.Services.ExternalProviders;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class ConfigurationArchiveServiceTests
{
    private const string Passphrase = "A portable test passphrase 2026!";

    [Fact]
    public async Task RoundTripAcrossDifferentKeyRingsPreservesEverySecretAndRelation()
    {
        using var source = new Fixture(); using var destination = new Fixture(seed: false);
        var zip = await source.Service.ExportAsync(Passphrase);
        var result = await Read(destination, zip);
        Assert.False(result.IsLegacy); Assert.True(result.Preview.IsComplete);
        Assert.Equal(1, result.Preview.Sources); Assert.Equal(1, result.Preview.Indexers);
        Assert.True(result.Preview.Parameters > result.Preview.Settings);
        var c = result.Configuration;
        Assert.Equal("provider-secret", c.Providers.Single()["api_key"]!.GetValue<string>());
        Assert.Equal("source-secret", c.Sources.Single()["api_key"]!.GetValue<string>());
        Assert.Equal("app-secret", c.Applications.Single()["api_key_encrypted"]!.GetValue<string>());
        Assert.Equal(c.Providers[0]["key"]!.ToString(), c.Sources[0]["providerKey"]!.ToString());
        Assert.All(c.Categories.Concat(c.Mappings).Concat(c.Selections), r => Assert.Equal(c.Sources[0]["key"]!.ToString(), r["sourceKey"]!.ToString()));
        Assert.Equal(c.Sources[0]["key"]!.ToString(), c.Settings["ui"]["defaultFilterSourceId"]!.GetValue<string>());
        Assert.Equal(c.Applications[0]["key"]!.ToString(), c.Settings["ui"]["defaultFilterApplication"]!.GetValue<string>());
        foreach (var provider in source.Registry.List())
        {
            var exported = c.ExternalProviders.Single(e => e["provider_key"]!.GetValue<string>() == provider.ProviderKey);
            foreach (var field in provider.FieldsSchema)
                Assert.Equal(provider.ProviderKey + "-" + field.Key + "-secret", exported["auth_json"]![field.Key]!.GetValue<string>());
        }
        var local = destination.Protection.Protect(c.Sources[0]["api_key"]!.GetValue<string>());
        Assert.Equal("source-secret", destination.Protection.Unprotect(local));
        Assert.False(source.Protection.TryUnprotect(local, out _));
        Assert.False(c.Settings["ui"].ContainsKey("onboardingDone"));
        Assert.Equal(Convert.ToBase64String(new byte[32]), c.Settings["security"]["passwordHash"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(new byte[16]), c.Settings["security"]["passwordSalt"]!.GetValue<string>());
        var serialized = JsonSerializer.SerializeToUtf8Bytes(c, ConfigurationSchema.Json);
        Assert.DoesNotContain("ENC:", Encoding.UTF8.GetString(serialized));
        Assert.DoesNotContain("last_sync", Encoding.UTF8.GetString(serialized));
        Assert.DoesNotContain("NO_RELEASES_SENTINEL", Encoding.UTF8.GetString(serialized));
        Assert.DoesNotContain("CACHED_CATEGORY_SENTINEL", Encoding.UTF8.GetString(serialized));
        Assert.Single(c.Categories);
        Assert.StartsWith("ref-", c.Sources[0]["key"]!.ToString());
        Assert.DoesNotContain("source-secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ExportContainsExactlyTwoEncryptedEntriesAndFreshRandomness()
    {
        using var f = new Fixture(); var first = await f.Service.ExportAsync(Passphrase); var second = await f.Service.ExportAsync(Passphrase);
        var a = Entries(first); var b = Entries(second);
        Assert.Equal(new[] { "configuration.enc", "manifest.json" }, a.Keys.Order().ToArray());
        foreach (var secret in new[] { "provider-secret", "source-secret", "app-secret", Passphrase, "NO_RELEASES_SENTINEL", ".master-key", "ENC:" })
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(first));
        Assert.NotEqual(a["manifest.json"], b["manifest.json"]);
        Assert.NotEqual(a["configuration.enc"], b["configuration.enc"]);
        Assert.True(first.Length < 100_000);
    }

    [Fact]
    public async Task WrongPassphraseHasSafeExplicitError()
    {
        using var f = new Fixture(); var data = await f.Service.ExportAsync(Passphrase);
        await Error(() => Read(f, data, "An incorrect long passphrase!"), ConfigurationArchiveError.AuthenticationFailed);
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("manifest")]
    [InlineData("tag")]
    public async Task AuthenticatesCiphertextTagAndExactManifestBytes(string part)
    {
        using var f = new Fixture(); var entries = Entries(await f.Service.ExportAsync(Passphrase));
        if (part == "manifest") entries["manifest.json"] = Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(entries["manifest.json"]));
        else entries["configuration.enc"][part == "tag" ? ^1 : 0] ^= 1;
        await Error(() => Read(f, Archive(entries)), ConfigurationArchiveError.AuthenticationFailed);
    }

    [Theory]
    [InlineData("formatVersion", 3)]
    [InlineData("schemaVersion", 99)]
    public async Task RejectsFutureVersions(string field, int version)
    {
        using var f = new Fixture(); var entries = Entries(await f.Service.ExportAsync(Passphrase));
        var manifest = JsonNode.Parse(entries["manifest.json"])!; manifest[field] = version;
        entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
        await Error(() => Read(f, Archive(entries)), ConfigurationArchiveError.UnsupportedVersion);
    }

    [Theory]
    [InlineData("iterations")]
    [InlineData("salt")]
    [InlineData("nonce")]
    [InlineData("kdf")]
    [InlineData("cipher")]
    public async Task RejectsUnsafeCryptographicParametersBeforeDerivation(string field)
    {
        using var f = new Fixture(); var entries = Entries(await f.Service.ExportAsync(Passphrase));
        var manifest = JsonNode.Parse(entries["manifest.json"])!;
        manifest[field] = field == "iterations" ? JsonValue.Create(int.MaxValue) : JsonValue.Create("unsupported");
        entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
        await Error(() => Read(f, Archive(entries)), ConfigurationArchiveError.InvalidArchive);
    }

    [Theory]
    [InlineData("extra-entry")]
    [InlineData("duplicate-entry")]
    [InlineData("duplicate-json")]
    [InlineData("unknown-field")]
    [InlineData("truncated")]
    public async Task RejectsMalformedArchives(string mutation)
    {
        using var f = new Fixture(); var entries = Entries(await f.Service.ExportAsync(Passphrase));
        if (mutation == "truncated") { await Error(() => Read(f, [1, 2, 3]), ConfigurationArchiveError.InvalidArchive); return; }
        if (mutation == "extra-entry") entries["../unwanted.txt"] = [1];
        if (mutation == "unknown-field")
        {
            var manifest = JsonNode.Parse(entries["manifest.json"])!; manifest["extra"] = true;
            entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
        }
        if (mutation == "duplicate-json") entries["manifest.json"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(entries["manifest.json"]).Replace("{", "{\"formatVersion\":2,", StringComparison.Ordinal));
        if (mutation == "duplicate-entry")
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (var item in entries.Append(new KeyValuePair<string, byte[]>("manifest.json", entries["manifest.json"])))
                    using (var stream = zip.CreateEntry(item.Key).Open()) stream.Write(item.Value);
            await Error(() => Read(f, memory.ToArray()), ConfigurationArchiveError.InvalidArchive); return;
        }
        await Error(() => Read(f, Archive(entries)), ConfigurationArchiveError.InvalidArchive);
    }

    [Theory]
    [InlineData("dangling")]
    [InlineData("duplicate-key")]
    [InlineData("business-data")]
    [InlineData("null-section")]
    [InlineData("unknown-setting")]
    [InlineData("null-string")]
    [InlineData("wrong-flag")]
    [InlineData("category-reference")]
    [InlineData("unknown-auth-field")]
    public async Task RejectsAuthenticatedButIncoherentModel(string mutation)
    {
        using var f = new Fixture(); var data = await f.Service.ExportAsync(Passphrase);
        var altered = RewriteModel(data, node =>
        {
            switch (mutation)
            {
                case "dangling": node["sources"]![0]!["providerKey"] = "missing"; break;
                case "duplicate-key": node["providers"]!.AsArray().Add(node["providers"]![0]!.DeepClone()); break;
                case "business-data": node["releases"] = new JsonArray(); break;
                case "null-section": node["sources"] = null; break;
                case "unknown-setting": node["settings"]!["general"]!["arbitrary"] = "secret"; break;
                case "null-string": node["settings"]!["security"]!["passwordHash"] = null; break;
                case "wrong-flag": node["sources"]![0]!["enabled"] = 2; break;
                case "category-reference": node["categories"]![0]!["sourceKey"] = "missing"; break;
                case "unknown-auth-field": node["externalProviders"]![0]!["auth_json"]!["unexpected"] = "value"; break;
            }
        });
        var error = await Assert.ThrowsAsync<ConfigurationArchiveException>(() => Read(f, altered));
        Assert.Contains(error.Code, new[] { ConfigurationArchiveError.InvalidArchive, ConfigurationArchiveError.InvalidConfiguration });
    }

    [Fact]
    public async Task IncompleteConfigurationIsReportedWithoutBeingCalledComplete()
    {
        using var f = new Fixture(); using (var c = f.Db.Open()) c.Execute("UPDATE sources SET api_key=''; UPDATE providers SET api_key_encrypted=''");
        var read = await Read(f, await f.Service.ExportAsync(Passphrase));
        Assert.False(read.Preview.IsComplete); Assert.True(read.Preview.MissingSecrets >= 1);
    }

    [Fact]
    public async Task UnreadableLocalSecretAbortsExportInsteadOfSilentlyDroppingIt()
    {
        using var f = new Fixture(); using var other = new Fixture(seed: false);
        using (var c = f.Db.Open()) c.Execute("UPDATE sources SET api_key=@secret", new { secret = other.Protection.Protect("not-recoverable") });
        await Error(() => f.Service.ExportAsync(Passphrase), ConfigurationArchiveError.UnreadableSecrets);
    }

    [Fact]
    public async Task NormalizesLegacySettingsWithoutOverridingAuthoritativeInstances()
    {
        using var f = new Fixture(); using (var c = f.Db.Open())
        {
            c.Execute("DELETE FROM external_provider_instances WHERE provider_key='igdb'");
            f.Set(c, "igdb_client_id", f.Protection.Protect("legacy-id"));
            f.Set(c, "igdb_client_secret", f.Protection.Protect("legacy-secret"));
            f.Set(c, "igdb_enabled", "1");
            f.Set(c, "tmdb_api_key", f.Protection.Protect("stale-key"));
        }
        var model = (await Read(f, await f.Service.ExportAsync(Passphrase))).Configuration;
        Assert.Equal("legacy-secret", model.ExternalProviders.Single(x => x["provider_key"]!.GetValue<string>() == "igdb")["auth_json"]!["clientSecret"]!.GetValue<string>());
        Assert.Equal("tmdb-apiKey-secret", model.ExternalProviders.Single(x => x["provider_key"]!.GetValue<string>() == "tmdb")["auth_json"]!["apiKey"]!.GetValue<string>());
        Assert.Equal(1, model.ExternalProviders.Count(x => x["provider_key"]!.GetValue<string>() == "tmdb"));
    }

    [Fact]
    public async Task RejectsOversizedInflatedPayloadWithoutDecrypting()
    {
        using var f = new Fixture(); var entries = Entries(await f.Service.ExportAsync(Passphrase));
        entries["configuration.enc"] = new byte[ConfigurationArchiveService.MaxConfigurationBytes + 17];
        var compressed = Archive(entries, CompressionLevel.SmallestSize);
        Assert.True(compressed.Length < 100_000);
        await Error(() => Read(f, compressed), ConfigurationArchiveError.SizeLimitExceeded);
    }

    [Fact]
    public async Task BoundedInputAndCancellationAreEnforced()
    {
        using var f = new Fixture();
        using var tooLarge = new RepeatingStream(ConfigurationArchiveService.MaxLegacyBytes + 1L);
        await Error(() => f.Service.ReadAsync(tooLarge, Passphrase), ConfigurationArchiveError.SizeLimitExceeded);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ExportAsync(Passphrase, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ReadAsync(new MemoryStream([1]), Passphrase, cancel.Token));
    }

    [Fact]
    public async Task ReadsLegacyArchiveWithoutLibraryOrActiveDatabaseWrites()
    {
        using var f = new Fixture(); var legacy = f.LegacyArchive();
        using var destination = new Fixture(seed: false);
        var before = File.ReadAllBytes(destination.Db.DbPath);
        var read = await Read(destination, legacy);
        Assert.True(read.IsLegacy); Assert.False(read.Preview.IsComplete); Assert.True(read.Preview.MissingSecrets > 0); Assert.NotEmpty(read.SecretsToReenter);
        Assert.Equal("", read.Configuration.Sources.Single()["api_key"]!.GetValue<string>());
        Assert.DoesNotContain("NO_RELEASES_SENTINEL", JsonSerializer.Serialize(read.Configuration, ConfigurationSchema.Json));
        Assert.Equal(before, File.ReadAllBytes(destination.Db.DbPath));
        using var c = destination.Db.Open(); Assert.Equal(0, c.ExecuteScalar<int>("SELECT count(*) FROM sources"));
        var same = await Read(f, legacy); Assert.True(same.Preview.IsComplete);
    }

    [Fact]
    public async Task LegacyChecksumMismatchIsRejected()
    {
        using var f = new Fixture(); var entries = Entries(f.LegacyArchive()); entries["feedarr.db"][100] ^= 1;
        await Error(() => Read(f, Archive(entries)), ConfigurationArchiveError.InvalidArchive);
    }

    [Fact]
    public async Task ExportAndV2ReadDoNotModifyDatabaseOrCreatePlaintextFiles()
    {
        using var f = new Fixture(); using var held = f.Db.Open();
        var before = held.Query<(string, string)>("SELECT key,value_json FROM app_settings ORDER BY key").ToArray();
        var files = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        var dbBytes = File.ReadAllBytes(f.Db.DbPath);
        var data = await f.Service.ExportAsync(Passphrase); await Read(f, data);
        Assert.Equal(before, held.Query<(string, string)>("SELECT key,value_json FROM app_settings ORDER BY key").ToArray());
        Assert.Equal(dbBytes, File.ReadAllBytes(f.Db.DbPath));
        Assert.Equal(files, Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Fact]
    public async Task LegacyPreInstanceSchemaAndOldProviderColumnAreConvertedInMemory()
    {
        using var f = new Fixture();
        var archive = f.LegacyArchive(c =>
        {
            c.Execute("ALTER TABLE providers RENAME COLUMN api_key_encrypted TO api_key; DROP TABLE external_provider_instances; DROP TABLE source_selected_categories");
            f.Set(c, "external", new ExternalSettings { TmdbApiKey = f.Protection.Protect("older-tmdb"), TmdbEnabled = true });
            c.Execute("INSERT OR REPLACE INTO app_settings(key,value_json,updated_at_ts) VALUES('igdb_client_id',@value,1)", new { value = f.Protection.Protect("raw-legacy-id") });
            f.Set(c, "igdb_client_secret", f.Protection.Protect("raw-legacy-secret"));
        });
        var read = await Read(f, archive);
        Assert.True(read.IsLegacy); Assert.True(read.Preview.IsComplete);
        Assert.Equal("provider-secret", read.Configuration.Providers.Single()["api_key"]!.GetValue<string>());
        Assert.Equal("older-tmdb", read.Configuration.ExternalProviders.Single(x => x["provider_key"]!.GetValue<string>() == "tmdb")["auth_json"]!["apiKey"]!.GetValue<string>());
        Assert.Empty(read.Configuration.Selections);
    }

    [Fact]
    public async Task WeakPassphraseAndExcessiveConfigurationAreRejected()
    {
        using var f = new Fixture();
        await Error(() => f.Service.ExportAsync("short"), ConfigurationArchiveError.InvalidPassphrase);
        using (var c = f.Db.Open())
            c.Execute("INSERT INTO app_settings(key,value_json,updated_at_ts) VALUES('oversized',@value,1)", new { value = new string('x', ConfigurationArchiveService.MaxConfigurationBytes) });
        await Error(() => f.Service.ExportAsync(Passphrase), ConfigurationArchiveError.SizeLimitExceeded);
    }

    [Fact]
    public async Task MultipleExternalInstancesKeepTheirEffectivePrecedence()
    {
        using var f = new Fixture();
        using (var c = f.Db.Open())
            c.Execute("INSERT INTO external_provider_instances(instance_id,provider_key,enabled,base_url,auth_json,options_json,created_at_ts,updated_at_ts) VALUES('newest','tmdb',1,'https://api.themoviedb.org/3',@auth,'{}',1,999)",
                new { auth = JsonSerializer.Serialize(new { apiKey = f.Protection.Protect("newest-secret") }) });
        var read = await Read(f, await f.Service.ExportAsync(Passphrase));
        var ordered = read.Configuration.ExternalProviders.Where(x => x["provider_key"]!.GetValue<string>() == "tmdb").OrderBy(x => x["priority"]!.GetValue<long>()).ToArray();
        Assert.Equal(2, ordered.Length);
        Assert.Equal("newest-secret", ordered[0]["auth_json"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("tmdb-apiKey-secret", ordered[1]["auth_json"]!["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task RejectsOversizedManifestAndForgedZipEntryCount()
    {
        using var f = new Fixture();
        var original = await f.Service.ExportAsync(Passphrase);
        var entries = Entries(original);
        entries["manifest.json"] = Encoding.UTF8.GetBytes(new string(' ', 4097));
        await Error(() => Read(f, Archive(entries, CompressionLevel.SmallestSize)), ConfigurationArchiveError.SizeLimitExceeded);
        original[^12] = 255; original[^11] = 255;
        await Error(() => Read(f, original), ConfigurationArchiveError.InvalidArchive);
    }

    private static Task<ConfigurationArchiveReadResult> Read(Fixture f, byte[] bytes, string passphrase = Passphrase) => f.Service.ReadAsync(new MemoryStream(bytes), passphrase);
    private static async Task Error(Func<Task> action, ConfigurationArchiveError code)
    {
        var e = await Assert.ThrowsAsync<ConfigurationArchiveException>(action); Assert.Equal(code, e.Code);
        Assert.DoesNotContain("secret", e.Message.Replace("secrets", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
        Assert.Null(e.InnerException);
    }
    private static Dictionary<string, byte[]> Entries(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var stream = e.Open(); using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray(); });
    }
    private static byte[] Archive(Dictionary<string, byte[]> entries, CompressionLevel level = CompressionLevel.NoCompression)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var e in entries) using (var stream = zip.CreateEntry(e.Key, level).Open()) stream.Write(e.Value);
        return memory.ToArray();
    }
    private static byte[] RewriteModel(byte[] archive, Action<JsonNode> rewrite)
    {
        var entries = Entries(archive); var m = JsonNode.Parse(entries["manifest.json"])!;
        var key = Rfc2898DeriveBytes.Pbkdf2(Passphrase, Convert.FromBase64String(m["salt"]!.GetValue<string>()), 600_000, HashAlgorithmName.SHA256, 32);
        var encrypted = entries["configuration.enc"]; var plaintext = new byte[encrypted.Length - 16];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(Convert.FromBase64String(m["nonce"]!.GetValue<string>()), encrypted.AsSpan(0, plaintext.Length), encrypted.AsSpan(plaintext.Length), plaintext, entries["manifest.json"]);
        var node = JsonNode.Parse(plaintext)!; rewrite(node); plaintext = JsonSerializer.SerializeToUtf8Bytes(node);
        var nonce = RandomNumberGenerator.GetBytes(12); m["nonce"] = Convert.ToBase64String(nonce);
        entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(m); encrypted = new byte[plaintext.Length + 16];
        aes.Encrypt(nonce, plaintext, encrypted.AsSpan(0, plaintext.Length), encrypted.AsSpan(plaintext.Length), entries["manifest.json"]);
        entries["configuration.enc"] = encrypted; CryptographicOperations.ZeroMemory(key); return Archive(entries);
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "feedarr-v2-tests", Guid.NewGuid().ToString("N"));
        public Db Db { get; }
        public ApiKeyProtectionService Protection { get; }
        public IDataProtectionProvider DataProtection { get; }
        public ExternalProviderRegistry Registry { get; } = new();
        public ConfigurationArchiveService Service { get; }
        public Fixture(bool seed = true)
        {
            Directory.CreateDirectory(Root);
            var master = new FixtureMasterKey();
            DataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Root, "keys")), builder =>
            {
                builder.Services.AddSingleton<IMasterKeyProvider>(master);
                builder.Services.Configure<KeyManagementOptions>(options => options.XmlEncryptor = new AesXmlEncryptor(master.GetMasterKey()));
            });
            Protection = new ApiKeyProtectionService(DataProtection, NullLogger<ApiKeyProtectionService>.Instance);
            Db = new Db(OptionsFactory.Create(new AppOptions { DataDir = Root, DbFileName = "feedarr.db" }));
            new MigrationsRunner(Db, NullLogger<MigrationsRunner>.Instance).Run();
            Db.EnsureWalMode();
            if (seed) Seed();
            Service = new ConfigurationArchiveService(Db, Protection, Registry, new BackupValidationService());
        }
        public void Set(SqliteConnection c, string key, object value) => c.Execute("INSERT OR REPLACE INTO app_settings(key,value_json,updated_at_ts) VALUES(@key,@value,1)", new { key, value = JsonSerializer.Serialize(value, ConfigurationSchema.Json) });
        private void Seed()
        {
            using var c = Db.Open();
            c.Execute("INSERT INTO providers(id,type,name,base_url,api_key_encrypted,enabled,created_at_ts,updated_at_ts) VALUES(98,'jackett','test','https://indexer.example',@key,1,1,1)", new { key = Protection.Protect("provider-secret") });
            c.Execute("INSERT INTO sources(id,name,torznab_url,api_key,auth_mode,provider_id,created_at_ts,updated_at_ts) VALUES(135,'source','https://indexer.example/api',@key,'query',98,1,1)", new { key = Protection.Protect("source-secret") });
            c.Execute("INSERT INTO arr_applications(id,type,base_url,api_key_encrypted,is_default,created_at_ts,updated_at_ts) VALUES(89,'sonarr','http://sonarr:8989',@key,1,1,1)", new { key = Protection.Protect("app-secret") });
            c.Execute("INSERT INTO source_categories(source_id,cat_id,name) VALUES(135,2000,'Films'); INSERT INTO source_selected_categories VALUES(135,2000); INSERT INTO source_category_mappings(source_id,cat_id,group_key,group_label,created_at_ts,updated_at_ts) VALUES(135,2000,'films','Films',1,1)");
            c.Execute("INSERT INTO source_categories(source_id,cat_id,name) VALUES(135,9999,'CACHED_CATEGORY_SENTINEL')");
            c.Execute("INSERT INTO releases(source_id,guid,title,created_at_ts,poster_file) VALUES(135,'test','NO_RELEASES_SENTINEL',1,'POSTER_SENTINEL.jpg')");
            Set(c, "general", new GeneralSettings()); Set(c, "arr", new ArrSettings()); Set(c, "maintenance", new MaintenanceSettings());
            Set(c, "ui", new UiSettings { OnboardingDone = true, DefaultFilterSourceId = "135", DefaultFilterApplication = "89" });
            Set(c, "security", new SecuritySettings { Authentication = "basic", Username = "test-admin", PasswordHash = Convert.ToBase64String(new byte[32]), PasswordSalt = Convert.ToBase64String(new byte[16]) });
            foreach (var p in Registry.List())
            {
                var auth = p.FieldsSchema.ToDictionary(f => f.Key, f => f.Secret ? Protection.Protect(p.ProviderKey + "-" + f.Key + "-secret") : p.ProviderKey + "-" + f.Key + "-secret");
                c.Execute("INSERT INTO external_provider_instances(instance_id,provider_key,enabled,base_url,auth_json,options_json,created_at_ts,updated_at_ts) VALUES(@id,@provider,1,@url,@auth,'{}',1,1)", new { id = Guid.NewGuid().ToString("N"), provider = p.ProviderKey, url = p.DefaultBaseUrl, auth = JsonSerializer.Serialize(auth) });
            }
        }
        public byte[] LegacyArchive(Action<SqliteConnection>? mutate = null)
        {
            using var c = Db.Open();
            var snapshot = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".db");
            c.Execute("VACUUM INTO @snapshot", new { snapshot });
            if (mutate is not null)
            {
                using var old = new SqliteConnection($"Data Source={snapshot};Pooling=False"); old.Open(); mutate(old);
            }
            var bytes = File.ReadAllBytes(snapshot);
            var info = JsonSerializer.SerializeToUtf8Bytes(new { version = "2.5.18-beta", backupFormatVersion = 1, dbSha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
            return Archive(new() { ["info.json"] = info, ["config.json"] = "{}"u8.ToArray(), ["feedarr.db"] = bytes });
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }
    private sealed class FixtureMasterKey : IMasterKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public byte[] GetMasterKey() => _key;
    }
    private sealed class RepeatingStream(long remaining) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var n = (int)Math.Min(count, remaining); remaining -= n; Array.Clear(buffer, offset, n); return n; }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
