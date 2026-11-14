using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
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
    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "feedarr-configuration-tests", Guid.NewGuid().ToString("N"));
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
            Service = new ConfigurationArchiveService(Db, Protection, Registry);
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
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }
    private sealed class FixtureMasterKey : IMasterKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public byte[] GetMasterKey() => _key;
    }
}
