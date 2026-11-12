using Dapper;
using Microsoft.Data.Sqlite;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Data.Repositories;

public sealed partial class ReleaseRepository
{
    internal ReleaseForPoster? MatchingSnapshotForShadow => _matchingSnapshot.Value;

    internal MatchingShadowState? ReadMatchingShadowState(long id)
    {
        // No Db.Open(): it is ReadWriteCreate. This connection cannot create a DB
        // or execute data writes, and never runs migrations or changes journal mode.
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _db.DbPath, Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1
        }.ToString());
        conn.Open();
        conn.Execute("PRAGMA query_only=ON");
        return conn.QueryFirstOrDefault<MatchingShadowState>("""
            SELECT r.id Id, r.entity_id EntityId, me.match_revision Revision,
                   r.title RawTitle, r.title_clean TitleClean, r.year Year, r.unified_category Category,
                   r.season Season, r.episode Episode,
                   COALESCE(me.tmdb_id,r.tmdb_id) TmdbId,
                   COALESCE(me.tmdb_media_type,r.tmdb_media_type) TmdbType,
                   COALESCE(me.tvdb_id,r.tvdb_id) TvdbId,
                   COALESCE(me.ext_provider,r.ext_provider) ExternalProvider,
                   COALESCE(me.ext_provider_id,r.ext_provider_id) ExternalId,
                   me.manual_match_provider ManualProvider,
                   CASE WHEN r.title_manual_override=1 THEN 1 ELSE 0 END ManualTitle,
                   CASE WHEN me.match_revision IS NULL AND instr(lower(COALESCE(r.poster_file,me.poster_file,'')),'-manual')>0 THEN 1 ELSE 0 END LegacyManualPoster
            FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id WHERE r.id=@id
            """, new { id });
    }
}
