using Feedarr.Api.Models;
using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;
using Feedarr.Api.Services.Matching;
using Xunit.Abstractions;

namespace Feedarr.Api.Tests;

public sealed class MatchingP6CTitleTests(ITestOutputHelper output)
{
    private static ProviderSearchCandidate Candidate(string title, int year = 2026, string id = "1814493")
    {
        var identity = new SearchIdentity("tmdb", SearchMediaType.Movie, id);
        return new(identity, title, year, OfficialTitles: new[] {
            new OfficialProviderTitle(title, identity, ProviderTitleKind.Original, ProviderTitleSource.SearchOriginalTitle) });
    }
    private static CandidateRankingResult Rank(string raw, ProviderSearchCandidate candidate, UnifiedCategory category = UnifiedCategory.Film)
    {
        var queries = SearchCandidateGenerator.Generate(new(raw, category));
        return SearchCandidateRanker.Rank(new(queries, category, SearchMediaType.Movie), new[] { candidate });
    }

    [Fact]
    public void AsterixQueryActuallyExecutedHasNoTechnicalResidue()
    {
        const string raw = "Asterix.The.Mansion.Of.The.Gods.Le.Domaine.des.Dieux.2014.MULTi.1080p.HDRip.AAC.2.0.x264-XTGMKV";
        var queries = SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film));
        var query = Assert.Single(queries);
        output.WriteLine($"31753 query={query.Title};year={query.Year}");
        Assert.Equal(2014, query.Year);
        Assert.Equal("Asterix The Mansion Of The Gods Le Domaine des Dieux", query.Title);
        var candidate = Candidate("Asterix The Mansion of the Gods", 2014, "170522");
        candidate = candidate with { OfficialTitles = candidate.OfficialTitles!.Append(new("Le Domaine des Dieux", candidate.Identity,
            ProviderTitleKind.Localized, ProviderTitleSource.SearchTitle, "fr-FR")).ToArray() };
        var result = Rank(raw, candidate);
        Assert.Equal(CandidateDecision.Accept, result.Decision);
        Assert.NotNull(Assert.Single(result.Candidates).CompoundTitle);
    }

    [Theory]
    [InlineData("Mission.Impossible.1.1996.FRENCH.1080p.BluRay", "Mission: Impossible", 1996)]
    [InlineData("Harry.Potter.and.the.Deathly.Hallows.Part.2.2011.1080p.BluRay", "Harry Potter and the Deathly Hallows Part II", 2011)]
    [InlineData("Gladiator.t02.2024.FRENCH.1080p.BluRay", "Gladiator II", 2024)]
    [InlineData("Les.Enfants.du.Paradis.Version.Restauree.1945.FRENCH.1080p.BluRay", "Les Enfants du Paradis", 1945)]
    [InlineData("Macbeth.Version.Longue.1971.FRENCH.1080p.BluRay", "Macbeth", 1971)]
    [InlineData("Macbeth.1971.Version.Longue.FRENCH.1080p.BluRay", "Macbeth", 1971)]
    [InlineData("Les.Enfants.du.Paradis.1945.Version.Restauree.FRENCH.1080p.BluRay", "Les Enfants du Paradis", 1945)]
    public void OfficialVideoVariantsRequireReliableYear(string raw, string official, int year)
    {
        var result = Rank(raw, Candidate(official, year));
        output.WriteLine(string.Join(";", result.Candidates.SelectMany(c => c.Contradictions.Select(x => x.Code))));
        Assert.Equal(CandidateDecision.Accept, result.Decision);
        if (raw.IndexOf(year.ToString(), StringComparison.Ordinal) > raw.IndexOf("Version", StringComparison.Ordinal) || !raw.Contains("Version"))
            Assert.Contains(Assert.Single(result.Candidates).PositiveReasons, p => p.EndsWith("warning") || p == "official-numeric-variant-exact");
        Assert.NotEqual(CandidateDecision.Accept, Rank(raw, Candidate(official, year + 8)).Decision);
    }

    public static IEnumerable<object[]> Duos()
    {
        yield return new object[] { 31971, "Les.Duos.Impossibles.De.Jeremy.Ferrari.11.2026.FRENCH.1080p.WEB.10bits.EAC3.5.1.H265-SUPERFLU" };
        yield return new object[] { 31972, "Les.Duos.Impossibles.De.Jeremy.Ferrari.11.2026.FRENCH.1080p.WEB.EAC3.5.1.x264-SUPERFLU" };
        yield return new object[] { 31973, "Les.Duos.Impossibles.De.Jeremy.Ferrari.11.Part.2.2026.FRENCH.1080p.WEB.10bits.EAC3.5.1.H265-SUPERFLU" };
        yield return new object[] { 31974, "Les.Duos.Impossibles.De.Jeremy.Ferrari.11.Part.2.2026.FRENCH.1080p.WEB.EAC3.5.1.x264-SUPERFLU" };
        yield return new object[] { 31975, "Les.duos.impossibles.de.Jeremy.Ferrari.11eme.edition.2026.FRENCH.1080p.WEB.10bits.EAC3.5.1.H265-F0XY" };
        yield return new object[] { 31976, "Les.duos.impossibles.de.Jeremy.Ferrari.11eme.edition.2026.FRENCH.2160p.WEB.10bits.EAC3.5.1.H265-F0XY" };
        yield return new object[] { 31977, "Les.duos.impossibles.de.Jeremy.Ferrari.11eme.edition.2eme.soiree.2026.FRENCH.1080p.WEBRip.EAC3.5.1.x264-PiCKLES" };
    }
    [Theory, MemberData(nameof(Duos))]
    public void NumberedEventAndItsBroadcastSegments(int release, string raw)
    {
        // Explicit simulated provider title; audit provides candidate ID, not captured aliases.
        var candidate = Candidate("Les Duos impossibles de Jérémy Ferrari 11e édition");
        var result = Rank(raw, candidate, UnifiedCategory.Spectacle);
        output.WriteLine($"{release}: {result.Decision}");
        Assert.Equal(CandidateDecision.Accept, result.Decision);
        Assert.NotEqual(CandidateDecision.Accept, Rank(raw, Candidate("Les Duos impossibles de Jérémy Ferrari 12e édition"), UnifiedCategory.Spectacle).Decision);
    }
    [Theory]
    [InlineData("Gladiator.t02.2000.1080p.BluRay", "Gladiator", 2000)]
    [InlineData("Harry.Potter.and.the.Deathly.Hallows.Part.2.2011.1080p.BluRay", "Harry Potter and the Deathly Hallows Part 1", 2011)]
    [InlineData("Example.Part.2.2026.1080p.BluRay", "Example", 2026)]
    public void TrueSequelsCannotLoseTheirNumber(string raw, string title, int year)
        => Assert.NotEqual(CandidateDecision.Accept, Rank(raw, Candidate(title, year)).Decision);

    [Fact]
    public void UnknownEventScopeAndHomonymsRemainUnresolved()
    {
        const string raw = "Les.Duos.Impossibles.De.Jeremy.Ferrari.11.Part.2.2026.1080p.WEB.x264";
        Assert.NotEqual(CandidateDecision.Accept, Rank(raw, Candidate("Les Duos impossibles de Jérémy Ferrari 11"), UnifiedCategory.Spectacle).Decision);
        var queries = SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Spectacle));
        var c = Candidate("Les Duos impossibles de Jérémy Ferrari 11e édition");
        var other = Candidate(c.Title, id: "999");
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(new(queries, UnifiedCategory.Spectacle, SearchMediaType.Movie), new[] { c, other }).Decision);
    }
    [Theory]
    [InlineData("rss")]
    [InlineData("torznab")]
    public async Task PersistedAsterixUsesFreshQueriesAndExistingPublication(string source)
    {
        var requests = new List<string>();
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit, completeTmdbMetadata: true,
            tmdbResponse: (request, _) => {
                var path = request.RequestUri!.AbsolutePath;
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                object? response = null;
                if (path.EndsWith("/search/movie"))
                {
                    requests.Add(query["query"].ToString());
                    Assert.Equal("Asterix The Mansion Of The Gods Le Domaine des Dieux", query["query"].ToString());
                    object[] results = query["primary_release_year"] == "2014" ? new[] { new {
                        id = 170522, title = query["language"] == "fr-FR" ? "Le Domaine des Dieux" : "Asterix The Mansion of the Gods",
                        original_title = "Asterix The Mansion of the Gods", release_date = "2014-01-01", poster_path = "/fr.jpg" } } : Array.Empty<object>();
                    response = new { page = 1, total_pages = results.Length == 0 ? 0 : 1, total_results = results.Length, results };
                }
                if (path.EndsWith("/movie/170522")) response = new { id = 170522, title = "Le Domaine des Dieux",
                    original_title = "Asterix The Mansion of the Gods", release_date = "2014-01-01", overview = "Fixture", poster_path = "/fr.jpg", runtime = 85 };
                return Task.FromResult<HttpResponseMessage?>(response is null ? null : new(HttpStatusCode.OK) {
                    Content = new StringContent(JsonSerializer.Serialize(response)) });
            });
        var id = rig.IngestRelease("Asterix.The.Mansion.Of.The.Gods.Le.Domaine.des.Dieux.2014.MULTi.1080p.HDRip.AAC.2.0.x264-XTGMKV", UnifiedCategory.Animation);
        using (var connection = rig.Database.Open())
        {
            connection.Execute("UPDATE sources SET source_kind=@source WHERE id=1", new { source });
            var cat = source == "rss" ? 900 : 2000;
            connection.Execute("UPDATE releases SET title_clean='Ancien parsing avec résidus',year=NULL,category_id=@cat,category_ids=@cats,std_category_id=NULL,spec_category_id=NULL WHERE id=@id", new { id, cat, cats = cat.ToString() });
        }
        var result = await rig.FetchAsync(id);
        Assert.True(result.Ok, JsonSerializer.Serialize(result.Body));
        Assert.NotEmpty(requests);
        Assert.Equal(170522, rig.GetReleaseForPoster(id)!.TmdbId);
        Assert.NotNull(rig.GetReleaseForPoster(id)!.PosterFile);
        Assert.Equal("Ancien parsing avec résidus", rig.GetReleaseForPoster(id)!.TitleClean);
    }

    [Theory]
    [InlineData("9/11", "9:11")]
    [InlineData("4/18", "418")]
    public void NumericVariantsNeverEraseSignificantPunctuation(string query, string official)
    {
        var result = Rank(query + ".2026.1080p.WEB.x264", Candidate(official));
        Assert.Contains(Assert.Single(result.Candidates).Contradictions, c => c.Code == "title-numeric-punctuation-mismatch");
    }

    [Fact]
    public void GamesAndUnreliableYearsDoNotUseVideoRecognition()
    {
        var movie = Candidate("Example II");
        var query = new SearchCandidate("Example t02", null, CandidateYearEvidence.None, SearchCandidateOrigin.Primary);
        Assert.Null(OfficialCompoundTitle.Recognize(query, new(new[] { query }, UnifiedCategory.Film, SearchMediaType.Movie), movie, out _));
        var game = movie with { Identity = new("igdb", SearchMediaType.Game, "1") };
        query = query with { Year = 2026, YearEvidence = CandidateYearEvidence.Parsed };
        Assert.Null(OfficialCompoundTitle.Recognize(query, new(new[] { query }, UnifiedCategory.JeuWindows, SearchMediaType.Game), game, out _));
    }

}
