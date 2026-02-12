using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Security;
using System.Linq;
using System.Text.RegularExpressions;

namespace Feedarr.Api.Data.Repositories;

public sealed class ProviderRepository
{
    private readonly Db _db;
    private readonly IApiKeyProtectionService _keyProtection;
    private static readonly Regex ProwlarrTorznabRegex = new(
        @"/\d+/api/?(\?|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ProviderRepository(Db db, IApiKeyProtectionService keyProtection)
    {
        _db = db;
        _keyProtection = keyProtection;
    }

    public IEnumerable<ProviderListItem> List()
    {
        using var conn = _db.Open();
        return conn.Query<ProviderListItem>(
            """
            SELECT
                p.id as Id,
                p.type as Type,
                p.name as Name,
                p.base_url as BaseUrl,
                CASE WHEN p.api_key IS NULL OR p.api_key = '' THEN 0 ELSE 1 END as HasApiKey,
                p.enabled as Enabled,
                p.last_test_ok_at_ts as LastTestOkAt,
                p.created_at_ts as CreatedAt,
                p.updated_at_ts as UpdatedAt,
                (SELECT COUNT(1) FROM sources s WHERE s.provider_id = p.id) as LinkedSources
            FROM providers p
            ORDER BY p.id DESC;
            """
        );
    }

    public Provider? Get(long id)
    {
        using var conn = _db.Open();
        var provider = conn.QueryFirstOrDefault<Provider>(
            """
            SELECT
                id as Id,
                type as Type,
                name as Name,
                base_url as BaseUrl,
                api_key as ApiKey,
                enabled as Enabled,
                last_test_ok_at_ts as LastTestOkAt,
                created_at_ts as CreatedAt,
                updated_at_ts as UpdatedAt
            FROM providers
            WHERE id = @id;
            """,
            new { id }
        );

        // Déchiffrer la clé API si présente
        if (provider is not null && !string.IsNullOrEmpty(provider.ApiKey))
            provider.ApiKey = _keyProtection.Unprotect(provider.ApiKey);

        return provider;
    }

    public long Create(string type, string name, string baseUrl, string apiKey, bool enabled)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var encryptedKey = _keyProtection.Protect(apiKey);

        return conn.ExecuteScalar<long>(
            """
            INSERT INTO providers(type, name, base_url, api_key, enabled, created_at_ts, updated_at_ts)
            VALUES (@type, @name, @baseUrl, @apiKey, @enabled, @now, @now);
            SELECT last_insert_rowid();
            """,
            new { type, name, baseUrl, apiKey = encryptedKey, enabled = enabled ? 1 : 0, now }
        );
    }

    public long UpsertByType(string type, string name, string baseUrl, string apiKey, bool enabled)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var existingId = conn.ExecuteScalar<long?>(
            """
            SELECT id
            FROM providers
            WHERE LOWER(type) = LOWER(@type)
            ORDER BY id ASC
            LIMIT 1;
            """,
            new { type });

        var encryptedKey = _keyProtection.Protect(apiKey);

        if (existingId.HasValue)
        {
            conn.Execute(
                """
                UPDATE providers
                SET name = @name,
                    base_url = @baseUrl,
                    api_key = @apiKey,
                    enabled = @enabled,
                    updated_at_ts = @now
                WHERE id = @id;
                """,
                new
                {
                    id = existingId.Value,
                    name,
                    baseUrl,
                    apiKey = encryptedKey,
                    enabled = enabled ? 1 : 0,
                    now
                });

            return existingId.Value;
        }

        return conn.ExecuteScalar<long>(
            """
            INSERT INTO providers(type, name, base_url, api_key, enabled, created_at_ts, updated_at_ts)
            VALUES (@type, @name, @baseUrl, @apiKey, @enabled, @now, @now);
            SELECT last_insert_rowid();
            """,
            new
            {
                type,
                name,
                baseUrl,
                apiKey = encryptedKey,
                enabled = enabled ? 1 : 0,
                now
            });
    }

    public bool Update(long id, string type, string name, string baseUrl, string? apiKeyOrNullIfNoChange)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        int rows;
        if (apiKeyOrNullIfNoChange is null)
        {
            rows = conn.Execute(
                """
                UPDATE providers
                SET type = @type,
                    name = @name,
                    base_url = @baseUrl,
                    updated_at_ts = @now
                WHERE id = @id;
                """,
                new { id, type, name, baseUrl, now }
            );
        }
        else
        {
            var encryptedKey = _keyProtection.Protect(apiKeyOrNullIfNoChange);
            rows = conn.Execute(
                """
                UPDATE providers
                SET type = @type,
                    name = @name,
                    base_url = @baseUrl,
                    api_key = @apiKey,
                    updated_at_ts = @now
                WHERE id = @id;
                """,
                new { id, type, name, baseUrl, apiKey = encryptedKey, now }
            );
        }

        return rows > 0;
    }

    public bool SetEnabled(long id, bool enabled)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = conn.Execute(
            """
            UPDATE providers
            SET enabled = @enabled,
                updated_at_ts = @now
            WHERE id = @id;
            """,
            new { id, enabled = enabled ? 1 : 0, now }
        );
        return rows > 0;
    }

    public void UpdateLastTestOk(long id)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            """
            UPDATE providers
            SET last_test_ok_at_ts = @now,
                updated_at_ts = @now
            WHERE id = @id;
            """,
            new { id, now }
        );
    }

    public int Delete(long id)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE sources SET provider_id = NULL WHERE provider_id = @id", new { id });
        return conn.Execute("DELETE FROM providers WHERE id = @id", new { id });
    }

    public int BootstrapFromSources()
    {
        using var conn = _db.Open();

        var providersExisting = conn.Query(
            "SELECT id, type, base_url as baseUrl FROM providers;"
        ).ToList();

        var sources = conn.Query(
            "SELECT id, name, torznab_url as torznabUrl, api_key as apiKey FROM sources;"
        ).ToList();

        var created = 0;
        foreach (var src in sources)
        {
            var torznabUrl = Convert.ToString(src.torznabUrl) ?? "";
            if (string.IsNullOrWhiteSpace(torznabUrl)) continue;

            var type = DetectTypeFromTorznabUrl(torznabUrl);
            if (string.IsNullOrWhiteSpace(type)) continue;

            var baseUrl = ExtractBaseUrl(type, torznabUrl);
            if (string.IsNullOrWhiteSpace(baseUrl)) continue;

            var apiKey = Convert.ToString(src.apiKey) ?? "";
            if (string.IsNullOrWhiteSpace(apiKey)) continue;

            // Déchiffrer la clé source si elle est chiffrée
            var decryptedKey = _keyProtection.Unprotect(apiKey);

            var exists = providersExisting.Any(p =>
                string.Equals(Convert.ToString(p.type), type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Convert.ToString(p.baseUrl), baseUrl, StringComparison.OrdinalIgnoreCase));

            if (exists) continue;

            // Chiffrer la clé pour le nouveau provider
            var encryptedKey = _keyProtection.Protect(decryptedKey);
            var name = type.Equals("prowlarr", StringComparison.OrdinalIgnoreCase) ? "Prowlarr" : "Jackett";
            var id = conn.ExecuteScalar<long>(
                """
                INSERT INTO providers(type, name, base_url, api_key, enabled, created_at_ts, updated_at_ts)
                VALUES (@type, @name, @baseUrl, @apiKey, 1, @now, @now);
                SELECT last_insert_rowid();
                """,
                new { type, name, baseUrl, apiKey = encryptedKey, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
            );
            created++;
            providersExisting.Add(new { id, type, baseUrl });
        }

        // Try to link existing sources to providers (Jackett + Prowlarr).
        var providers = conn.Query(
            "SELECT id, type, base_url as baseUrl FROM providers;"
        );
        foreach (var p in providers)
        {
            var type = Convert.ToString(p.type) ?? "";
            var baseUrl = Convert.ToString(p.baseUrl) ?? "";
            if (string.IsNullOrWhiteSpace(baseUrl)) continue;
            if (type.Equals("jackett", StringComparison.OrdinalIgnoreCase))
            {
                var pattern = $"{baseUrl.TrimEnd('/')}/api/v2.0/indexers/%";
                conn.Execute(
                    "UPDATE sources SET provider_id = @pid WHERE provider_id IS NULL AND torznab_url LIKE @pattern",
                    new { pid = (long)p.id, pattern }
                );
            }
            else if (type.Equals("prowlarr", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedBase = baseUrl.TrimEnd('/');
                if (normalizedBase.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
                    normalizedBase = normalizedBase[..^"/api/v1".Length].TrimEnd('/');
                var pattern = $"{normalizedBase}/%/api%";
                conn.Execute(
                    "UPDATE sources SET provider_id = @pid WHERE provider_id IS NULL AND torznab_url LIKE @pattern",
                    new { pid = (long)p.id, pattern }
                );
            }
        }

        return created;
    }

    private static string? DetectTypeFromTorznabUrl(string torznabUrl)
    {
        if (torznabUrl.Contains("/api/v2.0/indexers/", StringComparison.OrdinalIgnoreCase))
            return "jackett";
        if (ProwlarrTorznabRegex.IsMatch(torznabUrl) ||
            torznabUrl.Contains("/api/v1/search", StringComparison.OrdinalIgnoreCase) ||
            torznabUrl.Contains("prowlarr", StringComparison.OrdinalIgnoreCase))
            return "prowlarr";
        return null;
    }

    private static string ExtractBaseUrl(string type, string torznabUrl)
    {
        if (string.IsNullOrWhiteSpace(torznabUrl)) return "";
        if (type.Equals("jackett", StringComparison.OrdinalIgnoreCase))
        {
            var marker = "/api/v2.0/indexers/";
            var idx = torznabUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return torznabUrl[..idx].TrimEnd('/');
            return torznabUrl.TrimEnd('/');
        }
        if (type.Equals("prowlarr", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = torznabUrl;
            var qidx = baseUrl.IndexOf('?', StringComparison.Ordinal);
            if (qidx >= 0) baseUrl = baseUrl[..qidx];
            baseUrl = baseUrl.TrimEnd('/');

            var v1Marker = "/api/v1/search";
            var v1Idx = baseUrl.IndexOf(v1Marker, StringComparison.OrdinalIgnoreCase);
            if (v1Idx > 0) return baseUrl[..v1Idx].TrimEnd('/');

            var match = Regex.Match(baseUrl, @"^(?<base>.+)/\d+/api$", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups["base"].Value.TrimEnd('/');

            return baseUrl.TrimEnd('/');
        }
        return torznabUrl.TrimEnd('/');
    }
}
