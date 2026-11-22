using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Torznab;

namespace Feedarr.Api.Tests;

public sealed class RssMatchingEvidenceTests
{
    private const string Plot = "A retired botanist returns to her mountain village after receiving a mysterious letter from an estranged childhood friend. Together with a local historian she discovers an abandoned observatory, follows a trail of forgotten diaries and uncovers the truth about a missing expedition that changed their families forever.";
    private static CandidateRankingContext Context(int? year = 2020) => new(
        new[] { new SearchCandidate("Shared Title", year, year.HasValue ? CandidateYearEvidence.Parsed : CandidateYearEvidence.None, SearchCandidateOrigin.Primary) },
        UnifiedCategory.Film, SearchMediaType.Movie);
    private static ProviderSearchCandidate Movie(string id, string plot = Plot, int year = 2020) =>
        new(new("tmdb", SearchMediaType.Movie, id), "Shared Title", year, OfficialOverviews: new[] { plot });
    private static RssMatchingEvidence Evidence(int id = 1, string? plot = Plot) => new(1, id, null, null, plot);

    [Fact]
    public async Task ProductionUsesRssAndSearchOverviewWithoutExtraEvidenceRequests()
    {
        var aliasCalls = 0;
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                if (request.RequestUri!.AbsolutePath.Contains("alternative_titles")) aliasCalls++;
                if (!request.RequestUri.AbsolutePath.EndsWith("/search/movie"))
                    return Task.FromResult<System.Net.Http.HttpResponseMessage?>(null);
                var payload = new { page = 1, total_pages = 1, total_results = 2, results = new[] {
                    new { id = 100, title = "The Matrix", original_title = "The Matrix", release_date = "1999-03-31", poster_path = "/tmdb/matrix-search.jpg", overview = Plot },
                    new { id = 101, title = "The Matrix", original_title = "The Matrix", release_date = "1999-01-01", poster_path = "/other.jpg", overview = "Another work with the same title and release year" }
                } };
                return Task.FromResult<System.Net.Http.HttpResponseMessage?>(new(System.Net.HttpStatusCode.OK) {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
                });
            });
        const string raw = "The.Matrix.1999.FRENCH.1080p.WEB.H264-TEAM";
        var id = rig.IngestRelease(raw, UnifiedCategory.Film);
        using (var db = rig.Database.Open())
        {
            var guid = db.QuerySingle<string>("SELECT guid FROM releases WHERE id=@id", new { id });
            rig.Repository.UpsertMany(1, "TEST", new[] { new TorznabItem {
                Guid = guid, Title = raw, Description = Plot, CategoryId = 2000, CategoryIds = new() { 2000 },
                Attrs = new() { ["tmdbid"] = "100" }
            } });
        }
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, System.Text.Json.JsonSerializer.Serialize(result.Body));
        Assert.Equal(100, rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.Equal(0, aliasCalls);
    }

    [Fact]
    public void RssSurvivesIngestionWithoutBecomingAnAssociation()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var items = new TorznabRssParser().Parse($"""
            <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel><item>
            <guid>rss-evidence</guid><title>Shared.Title.2020.1080p.WEB.H264-TEAM</title>
            <description><![CDATA[<p>{Plot}</p>]]></description>
            <torznab:attr name="category" value="2000"/>
            <torznab:attr name="tmdbid" value="1"/>
            <torznab:attr name="imdbid" value="tt1234567"/>
            <torznab:attr name="apikey" value="must-not-persist"/>
            </item></channel></rss>
            """);
        rig.Repository.UpsertMany(1, "TEST", items);
        using var db = rig.Database.Open();
        var id = db.QuerySingle<long>("SELECT id FROM releases WHERE guid='rss-evidence'");
        var state = rig.Repository.ReadMatchingShadowState(id)!;
        Assert.Null(state.TmdbId);
        Assert.False(state.Protected);
        Assert.DoesNotContain("must-not-persist", state.RssEvidenceJson!);
        var evidence = RssMatchingEvidence.Read(state.RssEvidenceJson)!;
        Assert.Equal(1, evidence.TmdbId);
        Assert.Equal("tt1234567", evidence.ImdbId);
        Assert.Equal(Plot, evidence.Synopsis);

        rig.Repository.SaveTmdbId(id, 42, "movie");
        items[0].Attrs["tmdbid"] = "99";
        rig.Repository.UpsertMany(1, "TEST", items);
        Assert.Equal(42, rig.Repository.ReadMatchingShadowState(id)!.TmdbId);
        // A new RSS item version cannot inherit stale claims from its previous description.
        items[0].Attrs.Clear(); items[0].Description = null;
        rig.Repository.UpsertMany(1, "TEST", items);
        var updated = RssMatchingEvidence.Read(rig.Repository.ReadMatchingShadowState(id)!.RssEvidenceJson)!;
        Assert.Null(updated.TmdbId); Assert.Null(updated.Synopsis);
    }

    [Fact]
    public void CorroborationSurvivesConfidenceSaturationAndMakesTheScoreDecisive()
    {
        var rows = new[] { Movie("1"), Movie("2", "A completely different story") };
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(Context(), rows).Decision);
        var ranked = SearchCandidateRanker.Rank(Context() with { RssEvidence = Evidence() }, rows);
        Assert.Equal(CandidateDecision.Accept, ranked.Decision);
        Assert.Equal(rows[0].Identity, ranked.SelectedIdentity);
        Assert.All(ranked.Candidates, c => Assert.InRange(c.Confidence, 0, 1));
        Assert.Contains("rss-id-and-official-synopsis-agree", ranked.Candidates[0].PositiveReasons);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(Context(null) with { RssEvidence = Evidence() }, new[] { rows[0] }).Decision);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(1, "Short tagline")]
    [InlineData(2, Plot)]
    public void IdAloneShortTextAndContradictorySynopsisCannotBreakATie(int id, string? plot)
    {
        var ranked = SearchCandidateRanker.Rank(Context() with { RssEvidence = Evidence(id, plot) },
            new[] { Movie("1"), Movie("2", "A completely different story") });
        Assert.Equal(CandidateDecision.Ambiguous, ranked.Decision);
    }

    [Fact]
    public void StrongYearContradictionsAndNumericTitlesStillBlockAcceptance()
    {
        var ranked = SearchCandidateRanker.Rank(Context() with { RssEvidence = Evidence() }, new[] { Movie("1", year: 1980) });
        Assert.Equal(CandidateDecision.Reject, ranked.Decision);
        var context = Context() with { Queries = new[] { new SearchCandidate("1917", 2019, CandidateYearEvidence.Parsed, SearchCandidateOrigin.Primary) }, RssEvidence = Evidence() };
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context,
            new[] { Movie("1", year: 2019) with { Title = "1917" } }).Decision);
    }

    [Fact]
    public void SharedOfficialExternalIdentityDoesNotEraseARealTie()
    {
        var imdb = new SearchIdentity("imdb", SearchMediaType.Movie, "tt1234567");
        var rows = new[] { Movie("1") with { KnownExternalIdentities = new[] { imdb } }, Movie("2") with { KnownExternalIdentities = new[] { imdb } } };
        var context = Context() with { RssEvidence = new(1, null, null, imdb.Id, Plot) };
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, rows).Decision);
        Assert.Null(RssMatchingEvidence.Read("{\"legacy\":true}"));
        Assert.Null(RssMatchingEvidence.Read("not json"));
        var conflicting = RssMatchingEvidence.From(new TorznabItem {
            Description = Plot, Attrs = new() { ["tmdbid"] = "1", ["tmdb"] = "2", ["imdb"] = "tt1234567" }
        });
        Assert.True(conflicting.ConflictingClaims);
        Assert.False(conflicting.Supports(rows[0]));
    }
}
