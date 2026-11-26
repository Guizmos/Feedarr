using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Xunit;

namespace Feedarr.Api.Tests;

public sealed class LocalIdentityReuseP4Tests
{
    public static IEnumerable<object[]> AuditDuplicates()
    {
        // Nine exact release fingerprints from the initial audit, frozen for offline replay.
        yield return new object[] { "Affaires.sensibles.S06E03.Mathieu.Haulbert.l.enfant.disparu.du.Verdon.2026.10.31.FRENCH.1080p.WEB.AAC.2.0.H264-BYOR" };
        yield return new object[] { "Affaires.sensibles.S06E02.L.etrange.disparition.de.Julie.Michel.2026.10.10.FRENCH.1080p.WEB.AAC.2.0.H264-BYOR" };
        yield return new object[] { "Affaires.sensibles.S06E01.Qui.a.tue.le.juge.Renaud.2026.10.03.FRENCH.1080p.WEB.AAC.2.0.H264-BYOR" };
        yield return new object[] { "66.minutes.S2026E13.Dimanche.22.11.2026.2026.11.22.FRENCH.1080p.WEB.AAC.2.0.H264-BYOR" };
        yield return new object[] { "66.minutes.grand.format.S2026E13.Ils.relevent.le.defi.du.Made.in.France.2026.11.22.FRENCH.1080p.WEB.AAC.2.0.H264-BYOR" };
        yield return new object[] { "C.Est.La.Famille.S08E02.FRENCH.1080p.WEB.AAC.2.0.H264-JTBC" };
        yield return new object[] { "Mauvaise.Influence.S01E06.VFQ.720p.WEB.AAC.2.0.H264-CaptQC" };
        yield return new object[] { "Mauvaise.Influence.S01E06.VFQ.1080p.WEB.AAC.2.0.H264-CaptQC" };
        yield return new object[] { "Esprit.Japon.Nolife.INTEGRALE.FRENCH.720p.WEB.x264.AAC-AnimesForAll" };
    }

    private static long Create(PosterMatchingContractTestRig rig, string title, bool donor, string category = "Serie", int identity = 134594)
    {
        var id = rig.CreateRelease(title, "Programme", null, Enum.Parse<UnifiedCategory>(category), category == "Film" ? "movie" : "series");
        using var db = rig.Database.Open();
        if (donor)
        {
            var entity = db.ExecuteScalar<long>("""
                INSERT INTO media_entities(unified_category,title_clean,created_at_ts,updated_at_ts,
                  tmdb_id,tmdb_media_type,ext_snapshot_version,ext_provider,ext_provider_id,ext_title,poster_file)
                VALUES(@category,@title,0,0,@identity,@type,1,'tmdb',CAST(@identity AS TEXT),'Programme','existing.jpg') RETURNING id
                """, new { category, title = Guid.NewGuid().ToString(), identity, type = category == "Film" ? "movie" : "tv" });
            db.Execute("""
                UPDATE releases SET entity_id=@entity,tmdb_id=@identity,tmdb_media_type=@type,
                  ext_snapshot_version=1,ext_provider='tmdb',ext_provider_id=CAST(@identity AS TEXT),poster_provider='tmdb',poster_provider_id=CAST(@identity AS TEXT),poster_lang='fr' WHERE id=@id
                """, new { entity, id, identity, type = category == "Film" ? "movie" : "tv" });
        }
        return id;
    }

    [Theory]
    [MemberData(nameof(AuditDuplicates))]
    public async Task NineAuditDuplicatesReuseDisabledSourceWithoutProviderCalls(string title)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var donor = Create(rig, title, true);
        var target = Create(rig, title, false, "Emission");
        using var db = rig.Database.Open();
        db.Execute("""
            INSERT INTO sources(id,name,torznab_url,api_key,created_at_ts,updated_at_ts) VALUES(2,'RSS','https://rss.invalid','',0,0);
            UPDATE releases SET source_id=2 WHERE id=@target;
            UPDATE sources SET enabled=0 WHERE id=1;
            """, new { target });
        Directory.CreateDirectory(rig.PostersDirectory);
        File.WriteAllBytes(Path.Combine(rig.PostersDirectory, "existing.jpg"), [1]);
        Assert.True((await rig.Posters.FetchPosterAsync(target, default, false)).Ok);
        Assert.Equal(rig.Repository.GetForPoster(donor)!.EntityId, rig.Repository.GetForPoster(target)!.EntityId);
        Assert.Equal(donor, db.ExecuteScalar<long>("SELECT donor_release_id FROM local_identity_reuse WHERE release_id=@target", new { target }));
        Assert.Empty(rig.TmdbSearchRequests);
        Assert.Equal(0, rig.TmdbDetailsCalls);
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT enabled FROM sources WHERE id=1"));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("legacy")]
    [InlineData("cascade")]
    [InlineData("title_override")]
    public void UnverifiedOrManualDonorsAreExcluded(string protection)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        const string title = "Programme.S01E02.1080p.WEB.x264";
        var donor = Create(rig, title, true); var target = Create(rig, title, false);
        using var db = rig.Database.Open();
        if (protection == "manual") db.Execute("UPDATE media_entities SET manual_match_provider='tmdb',manual_match_id='134594' WHERE id=(SELECT entity_id FROM releases WHERE id=@donor)", new { donor });
        if (protection == "legacy") db.Execute("UPDATE releases SET ext_snapshot_version=NULL WHERE id=@donor; UPDATE media_entities SET ext_snapshot_version=NULL WHERE id=(SELECT entity_id FROM releases WHERE id=@donor)", new { donor });
        if (protection == "title_override") db.Execute("UPDATE releases SET title_manual_override=1 WHERE id=@donor", new { donor });
        if (protection == "cascade") db.Execute("INSERT INTO local_identity_reuse VALUES(@donor,999,1,1,'tmdb','134594','test',0)", new { donor });
        Assert.False(rig.Repository.TryReuseLocalIdentity(target).Reused);
        Assert.Null(rig.Repository.GetForPoster(target)!.EntityId);
    }

    [Fact]
    public void ContradictoryIdentitiesAndRssClaimsAreRefused()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        const string title = "Programme.S01E02.1080p.WEB.x264";
        Create(rig, title, true); Create(rig, title, true, identity: 999);
        var target = Create(rig, title, false);
        Assert.Equal("contradictory_identities", rig.Repository.TryReuseLocalIdentity(target).Reason);
        using var db = rig.Database.Open();
        db.Execute("UPDATE releases SET raw_json=@json WHERE id=@target", new { target, json = "{\"Version\":1,\"TmdbId\":42}" });
        Assert.Equal("contradictory_claims", rig.Repository.TryReuseLocalIdentity(target).Reason);
    }

    [Fact]
    public void EstablishedAndManualTargetsArePreservedAndArrBadgesSupplyNoEvidence()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var donor = Create(rig, "Oppenheimer.2023.1080p.x265", true, "Film", 872585);
        Assert.Equal("target_protected", rig.Repository.TryReuseLocalIdentity(donor).Reason);
        foreach (var title in new[] { "Oppenheimer.2023.1080p.x265", "Overdose.2022.1080p.x265" })
        {
            var target = Create(rig, title, false, "Film");
            using var db = rig.Database.Open();
            db.Execute("UPDATE releases SET title_manual_override=1 WHERE id=@target", new { target });
            Assert.Equal("target_protected", rig.Repository.TryReuseLocalIdentity(target).Reason);
        }
        var badgeOnly = Create(rig, "Overdose.2022.1080p.x265", false, "Film");
        using var connection = rig.Database.Open();
        connection.Execute("INSERT INTO release_arr_status(release_id,in_radarr,checked_at_ts) VALUES(@badgeOnly,1,0)", new { badgeOnly });
        Assert.False(rig.Repository.TryReuseLocalIdentity(badgeOnly).Reused);
    }

    [Theory]
    [InlineData("JeuWindows", "igdb")]
    [InlineData("Anime", "jikan")]
    [InlineData("Audio", "musicbrainz")]
    [InlineData("Book", "openlibrary")]
    [InlineData("Comic", "comicvine")]
    public void NonVideoCategoriesReuseOnlyExactVerifiedReleaseScope(string category,string provider)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        const string title="Example.Deluxe.PC.Repack";
        var donor=Create(rig,title,true,category); var target=Create(rig,title,false,category);
        using var db=rig.Database.Open();
        db.Execute("UPDATE media_entities SET tmdb_id=NULL,tmdb_media_type=NULL,ext_provider=@provider,ext_provider_id='123' WHERE id=(SELECT entity_id FROM releases WHERE id=@donor)",new {donor,provider});
        db.Execute("UPDATE releases SET tmdb_id=NULL,tmdb_media_type=NULL,ext_provider=@provider,ext_provider_id='123',poster_provider=@provider,poster_provider_id='123' WHERE id=@donor",new {donor,provider});
        Assert.True(rig.Repository.TryReuseLocalIdentity(target).Reused);
        Assert.Equal(provider,rig.Repository.GetForPoster(target)!.ExtProvider);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalAliasClaimsMustAgreeWithTheVerifiedEntity(bool conflicting)
    {
        using var rig=new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        const string title="Programme.S01E02.1080p.x264";
        var donor=Create(rig,title,true); var target=Create(rig,title,false);
        using var db=rig.Database.Open();
        db.Execute("UPDATE media_entities SET tvdb_id=123,imdb_id='tt1234567' WHERE id=(SELECT entity_id FROM releases WHERE id=@donor)",new {donor});
        db.Execute("UPDATE releases SET raw_json=@json WHERE id=@target",new {target,json=conflicting ? "{\"Version\":1,\"TvdbId\":999,\"ImdbId\":\"tt1234567\"}" : "{\"Version\":1,\"TvdbId\":123,\"ImdbId\":\"tt1234567\"}"});
        Assert.Equal(!conflicting,rig.Repository.TryReuseLocalIdentity(target).Reused);
    }

    [Fact]
    public void InfohashReuseSharesStoreReferencesAndLeavesOtherEntityMembersUntouched()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var donor = Create(rig, "Dune.2021.1080p.x264", true, "Film", 438631);
        var target = Create(rig, "Dune.2021.720p.x265", false, "Film");
        var sibling = Create(rig, "Other.2021.720p.x265", false, "Film");
        using var db = rig.Database.Open();
        var oldEntity = db.ExecuteScalar<long>("INSERT INTO media_entities(unified_category,title_clean,created_at_ts,updated_at_ts) VALUES('Film','old',0,0) RETURNING id");
        db.Execute("UPDATE releases SET entity_id=@oldEntity WHERE id IN (@target,@sibling)", new { oldEntity, target, sibling });
        db.Execute("UPDATE releases SET info_hash=@hash WHERE id IN (@donor,@target)", new { donor, target, hash = new string('a',40) });
        db.Execute("UPDATE releases SET poster_store_dir='shared',poster_key='key',poster_hash='hash' WHERE id=@donor", new { donor });
        var result = rig.Repository.TryReuseLocalIdentity(target);
        Assert.True(result.Reused, result.Reason);
        Assert.Equal("infohash_and_scope",result.Reason);
        Assert.Equal("shared", db.ExecuteScalar<string>("SELECT store_dir FROM poster_store_refs WHERE release_id=@target", new { target }));
        Assert.Equal(oldEntity,rig.Repository.GetForPoster(sibling)!.EntityId);
        Assert.Null(rig.Repository.GetForPoster(sibling)!.TmdbId);
        Assert.Null(db.ExecuteScalar<int?>("SELECT ext_snapshot_version FROM releases WHERE id=@target",new {target}));
    }

    [Fact]
    public void ClearedManualAssociationNeverBecomesAnAutomaticRoot()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        const string title = "Programme.S01E02.1080p.x264";
        var donor = Create(rig,title,true); var target=Create(rig,title,false);
        using var db=rig.Database.Open();
        db.Execute("UPDATE media_entities SET retention_origin='manual' WHERE id=(SELECT entity_id FROM releases WHERE id=@donor)",new {donor});
        Assert.False(rig.Repository.TryReuseLocalIdentity(target).Reused);
    }

    [Theory]
    [InlineData("Dune", "Dune", false, false)]
    [InlineData("Dune.1080p.x264", "Dune.1080p.x264", false, false)]
    [InlineData("Dune.1984.1080p.x264", "Dune.2021.1080p.x264", true, false)]
    [InlineData("Dune.2021.Extended.1080p.x264", "Dune.2021.1080p.x264", true, false)]
    [InlineData("Programme.S01E01.1080p.x264", "Programme.S01E02.720p.x264", true, false)]
    [InlineData("Dune.2021.1080p.x264", "Dune.2021.720p.x265", true, true)]
    public void HashRequiresConcordantScope(string a, string b, bool hash, bool expected)
    {
        var category = a.StartsWith("Programme") ? "Serie" : "Film";
        var target = new ReleaseForPoster { Title = a, UnifiedCategory = category };
        var donor = new ReleaseForPoster { Title = b, UnifiedCategory = category };
        Assert.Equal(expected, LocalIdentityReuse.Corroborates(target, donor, hash));
    }

    [Theory]
    [InlineData("Game.Deluxe.PC.Repack", "Game.Standard.PC.Repack")]
    [InlineData("Game.PC.Repack", "Game.Switch.Repack")]
    [InlineData("Game.DLC.Repack", "Game.Repack")]
    public void IncompatibleGameEditionsAndPlatformsAreRefused(string a, string b)
        => Assert.False(LocalIdentityReuse.Corroborates(new() { Title = a, UnifiedCategory = "JeuWindows" }, new() { Title = b, UnifiedCategory = "JeuWindows" }, true));
}
