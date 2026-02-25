using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Data;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Posters;

public sealed record PosterTitleKey(
    string MediaType,
    string NormalizedTitle,
    int? Year,
    int? Season,
    int? Episode);

public sealed class PosterMatch
{
    public string Fingerprint { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string NormalizedTitle { get; set; } = "";
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? IdsJson { get; set; }
    public double Confidence { get; set; }
    public string? MatchSource { get; set; }
    public string? PosterFile { get; set; }
    public string? PosterProvider { get; set; }
    public string? PosterProviderId { get; set; }
    public string? PosterLang { get; set; }
    public string? PosterSize { get; set; }
    public long CreatedTs { get; set; }
    public long LastSeenTs { get; set; }
    public long? LastAttemptTs { get; set; }
    public string? LastError { get; set; }

    public PosterMatch() { }

    public PosterMatch(
        string fingerprint,
        string mediaType,
        string normalizedTitle,
        int? year,
        int? season,
        int? episode,
        string? idsJson,
        double confidence,
        string? matchSource,
        string? posterFile,
        string? posterProvider,
        string? posterProviderId,
        string? posterLang,
        string? posterSize,
        long createdTs,
        long lastSeenTs,
        long? lastAttemptTs,
        string? lastError)
    {
        Fingerprint = fingerprint;
        MediaType = mediaType;
        NormalizedTitle = normalizedTitle;
        Year = year;
        Season = season;
        Episode = episode;
        IdsJson = idsJson;
        Confidence = confidence;
        MatchSource = matchSource;
        PosterFile = posterFile;
        PosterProvider = posterProvider;
        PosterProviderId = posterProviderId;
        PosterLang = posterLang;
        PosterSize = posterSize;
        CreatedTs = createdTs;
        LastSeenTs = lastSeenTs;
        LastAttemptTs = lastAttemptTs;
        LastError = lastError;
    }
}

public sealed class PosterMatchCacheService
{
    private readonly Db _db;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PosterMatchCacheService(Db db)
    {
        _db = db;
    }

    public PosterMatch? TryGet(string fingerprint)
    {
        using var conn = _db.Open();
        return conn.QueryFirstOrDefault<PosterMatch>(
            """
            SELECT
                fingerprint as Fingerprint,
                media_type as MediaType,
                normalized_title as NormalizedTitle,
                year as Year,
                season as Season,
                episode as Episode,
                ids_json as IdsJson,
                confidence as Confidence,
                match_source as MatchSource,
                poster_file as PosterFile,
                poster_provider as PosterProvider,
                poster_provider_id as PosterProviderId,
                poster_lang as PosterLang,
                poster_size as PosterSize,
                created_ts as CreatedTs,
                last_seen_ts as LastSeenTs,
                last_attempt_ts as LastAttemptTs,
                last_error as LastError
            FROM poster_matches
            WHERE fingerprint = @fp
            LIMIT 1;
            """,
            new { fp = fingerprint }
        );
    }

    public PosterMatch? TryGetByTitleKey(string mediaType, string normalizedTitle, int? year)
    {
        using var conn = _db.Open();

        if (year.HasValue)
        {
            // Exact match (title + year): early return on hit.
            var exact = conn.QueryFirstOrDefault<PosterMatch>(
                """
                SELECT
                    fingerprint as Fingerprint,
                    media_type as MediaType,
                    normalized_title as NormalizedTitle,
                    year as Year,
                    season as Season,
                    episode as Episode,
                    ids_json as IdsJson,
                    confidence as Confidence,
                    match_source as MatchSource,
                    poster_file as PosterFile,
                    poster_provider as PosterProvider,
                    poster_provider_id as PosterProviderId,
                    poster_lang as PosterLang,
                    poster_size as PosterSize,
                    created_ts as CreatedTs,
                    last_seen_ts as LastSeenTs,
                    last_attempt_ts as LastAttemptTs,
                    last_error as LastError
                FROM poster_matches
                WHERE lower(media_type) = lower(@mt)
                  AND normalized_title = @title
                  AND year = @year
                ORDER BY confidence DESC, last_seen_ts DESC
                LIMIT 1;
                """,
                new { mt = mediaType, title = normalizedTitle, year }
            );
            if (exact is not null)
                return exact;
            // Fall through to title-only search as fallback.
        }

        return conn.QueryFirstOrDefault<PosterMatch>(
            """
            SELECT
                fingerprint as Fingerprint,
                media_type as MediaType,
                normalized_title as NormalizedTitle,
                year as Year,
                season as Season,
                episode as Episode,
                ids_json as IdsJson,
                confidence as Confidence,
                match_source as MatchSource,
                poster_file as PosterFile,
                poster_provider as PosterProvider,
                poster_provider_id as PosterProviderId,
                poster_lang as PosterLang,
                poster_size as PosterSize,
                created_ts as CreatedTs,
                last_seen_ts as LastSeenTs,
                last_attempt_ts as LastAttemptTs,
                last_error as LastError
            FROM poster_matches
            WHERE lower(media_type) = lower(@mt)
              AND normalized_title = @title
            ORDER BY confidence DESC, last_seen_ts DESC
            LIMIT 1;
            """,
            new { mt = mediaType, title = normalizedTitle }
        );
    }

    public void Upsert(PosterMatch match)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        match.LastSeenTs = now;
        if (match.CreatedTs == 0)
            match.CreatedTs = now;

        conn.Execute(
            """
            INSERT INTO poster_matches(
                fingerprint,
                media_type,
                normalized_title,
                year,
                season,
                episode,
                ids_json,
                confidence,
                match_source,
                poster_file,
                poster_provider,
                poster_provider_id,
                poster_lang,
                poster_size,
                created_ts,
                last_seen_ts,
                last_attempt_ts,
                last_error
            )
            VALUES(
                @Fingerprint,
                @MediaType,
                @NormalizedTitle,
                @Year,
                @Season,
                @Episode,
                @IdsJson,
                @Confidence,
                @MatchSource,
                @PosterFile,
                @PosterProvider,
                @PosterProviderId,
                @PosterLang,
                @PosterSize,
                @CreatedTs,
                @LastSeenTs,
                @LastAttemptTs,
                @LastError
            )
            ON CONFLICT(fingerprint) DO UPDATE SET
                media_type = excluded.media_type,
                normalized_title = excluded.normalized_title,
                year = excluded.year,
                season = excluded.season,
                episode = excluded.episode,
                ids_json = COALESCE(excluded.ids_json, poster_matches.ids_json),
                -- COALESCE would accept 0.0 (non-null) and overwrite a good value;
                -- use CASE WHEN so that an explicit zero is treated as "unset".
                confidence = CASE WHEN excluded.confidence > 0
                                  THEN excluded.confidence
                                  ELSE poster_matches.confidence
                             END,
                match_source = COALESCE(excluded.match_source, poster_matches.match_source),
                poster_file = COALESCE(excluded.poster_file, poster_matches.poster_file),
                poster_provider = COALESCE(excluded.poster_provider, poster_matches.poster_provider),
                poster_provider_id = COALESCE(excluded.poster_provider_id, poster_matches.poster_provider_id),
                poster_lang = COALESCE(excluded.poster_lang, poster_matches.poster_lang),
                poster_size = COALESCE(excluded.poster_size, poster_matches.poster_size),
                last_seen_ts = excluded.last_seen_ts,
                last_attempt_ts = COALESCE(excluded.last_attempt_ts, poster_matches.last_attempt_ts),
                last_error = excluded.last_error
            ;
            """,
            match
        );
    }

    public void TouchSeen(string fingerprint)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            "UPDATE poster_matches SET last_seen_ts = @ts WHERE fingerprint = @fp;",
            new { fp = fingerprint, ts = now }
        );
    }

    public void RecordAttempt(string fingerprint)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            "UPDATE poster_matches SET last_attempt_ts = @ts WHERE fingerprint = @fp;",
            new { fp = fingerprint, ts = now }
        );
    }

    public void RecordError(string fingerprint, string? error)
    {
        using var conn = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        conn.Execute(
            "UPDATE poster_matches SET last_attempt_ts = @ts, last_error = @err WHERE fingerprint = @fp;",
            new { fp = fingerprint, ts = now, err = error }
        );
    }

    public static string BuildFingerprint(PosterTitleKey key)
    {
        var baseValue = $"{key.MediaType}|{key.NormalizedTitle}|{key.Year?.ToString() ?? "null"}|{key.Season?.ToString() ?? "null"}|{key.Episode?.ToString() ?? "null"}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(baseValue));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string? SerializeIds(PosterMatchIds? ids)
    {
        if (ids is null || !ids.HasAny) return null;
        return JsonSerializer.Serialize(ids, JsonOpts);
    }

    public static PosterMatchIds? DeserializeIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PosterMatchIds>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}
