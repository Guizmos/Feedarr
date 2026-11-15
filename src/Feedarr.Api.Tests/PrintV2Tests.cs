using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;

namespace Feedarr.Api.Tests;

public sealed class PrintV2Tests
{
    private static long Create(PosterMatchingContractTestRig rig, bool comic) => rig.CreateRelease(
        comic ? "Batman #1" : "Clean Architecture 9780134494166", comic ? "Batman #1" : "Clean Architecture", 2020,
        comic ? UnifiedCategory.Comic : UnifiedCategory.Book, comic ? "comic" : "book");
    private static PosterContractScenario Scenario(bool comic) => comic ? PosterContractScenario.ComicComicVineHit : PosterContractScenario.BookGoogleBooksHit;
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishesIdentityMetadataPoster_AndRefreshesOnlyBoundIdentity(bool comic)
    {
        var searches = 0;
        Task<HttpResponseMessage?> Observe(HttpRequestMessage request, CancellationToken _) {
            if (request.RequestUri!.AbsolutePath.Contains("/search") || request.RequestUri.AbsolutePath.Contains("/volumes")) searches++;
            return Task.FromResult<HttpResponseMessage?>(null);
        }
        using var rig = new PosterMatchingContractTestRig(Scenario(comic), booksResponse: Observe, comicResponse: Observe);
        var id = Create(rig, comic);
        Assert.True((await rig.FetchAsync(id)).Ok);
        var before = rig.GetReleaseForPoster(id)!;
        Assert.Equal(comic ? "comicvine" : "googlebooks", before.ExtProvider);
        Assert.Equal(comic ? "issue:9900" : "book-900", before.ExtProviderId);
        Assert.Equal(comic ? "Batman #1" : "Clean Architecture", before.ExtTitle);
        Assert.NotNull(before.PosterFile); Assert.Null(before.TmdbId);
        var count = searches;
        Assert.True((await rig.Posters.FetchPosterAsync(id, default, false, skipIfExists: false)).Ok);
        var after = rig.GetReleaseForPoster(id)!;
        Assert.Equal(before.ExtProviderId, after.ExtProviderId);
        Assert.Equal(before.ExtTitle, after.ExtTitle);
        Assert.NotEqual(before.PosterFile, after.PosterFile);
        Assert.Equal(count, searches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompetingEditionsOrIssuesAreNotMerged(bool comic)
    {
        using var json = JsonDocument.Parse(comic ? """
          {"status_code":1,"number_of_total_results":2,"results":[
           {"id":1,"resource_type":"issue","name":"Batman #1","issue_number":"1","volume":{"name":"Batman"}},
           {"id":2,"resource_type":"issue","name":"Batman #1","issue_number":"1","volume":{"name":"Batman"}}]}
          """ : """
          {"totalItems":2,"items":[
           {"id":"a","volumeInfo":{"title":"Clean Architecture","industryIdentifiers":[{"type":"ISBN_13","identifier":"9780134494166"}]}},
           {"id":"b","volumeInfo":{"title":"Clean Architecture","industryIdentifiers":[{"type":"ISBN_13","identifier":"9780134494166"}]}}]}
          """);
        var rows = comic ? PrintCandidateAdapter.ComicVine(json.RootElement) : PrintCandidateAdapter.Google(json.RootElement);
        var context = PrintCandidateAdapter.Context(comic ? "Batman #1" : "Clean Architecture", comic ? UnifiedCategory.Comic : UnifiedCategory.Book,
            comic ? "Batman #1" : "Clean Architecture", null, comic ? null : "9780134494166");
        Assert.Equal(CandidateDecision.Ambiguous, SearchCandidateRanker.Rank(context, rows.Select(r => r.Candidate).ToArray()).Decision);
    }

    [Fact]
    public void WorkIsbnsDoNotIdentifyAnEdition_AndComicSeriesIsNotAnIssue()
    {
        using var work = JsonDocument.Parse("""{"numFound":1,"docs":[{"key":"/works/OL1W","title":"Clean Architecture","isbn":["9780134494166"],"author_name":["Robert Martin"]}]}""");
        var row = PrintCandidateAdapter.OpenLibrary(work.RootElement, false).Single().Candidate;
        Assert.Equal("works/OL1W", row.Identity.Id); Assert.Empty(row.Print!.Isbns);
        var context = PrintCandidateAdapter.Context("Clean Architecture", UnifiedCategory.Book, "Clean Architecture", null, "9780134494166");
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context, new[] { row }).Decision);
        using var series = JsonDocument.Parse("""{"status_code":1,"number_of_total_results":1,"results":[{"id":9900,"resource_type":"volume","name":"Batman #1","start_year":"2020"}]}""");
        var comic = PrintCandidateAdapter.ComicVine(series.RootElement).Single().Candidate;
        Assert.Equal("volume:9900", comic.Identity.Id);
        var issueContext = PrintCandidateAdapter.Context("Batman #1", UnifiedCategory.Comic, "Batman #1", null, null);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(issueContext, new[] { comic }).Decision);
    }

    [Fact]
    public void MissingCoverageAndUnknownComicResourceCannotProduceAcceptance()
    {
        using var partial = JsonDocument.Parse("""{"totalItems":11,"items":[]}""");
        Assert.Throws<InvalidDataException>(() => PrintCandidateAdapter.Google(partial.RootElement));
        using var unknown = JsonDocument.Parse("""{"status_code":1,"number_of_total_results":1,"results":[{"id":9900,"name":"Batman #1","cover_date":"2020-01-01"}]}""");
        var rows = PrintCandidateAdapter.ComicVine(unknown.RootElement);
        Assert.NotEqual(CandidateDecision.Accept, SearchCandidateRanker.Rank(PrintCandidateAdapter.Context("Batman #1", UnifiedCategory.Comic, "Batman #1", null, null), rows.Select(r => r.Candidate).ToArray()).Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualIdentityIsPreservedWithoutSearch(bool comic)
    {
        var requests = 0;
        Task<HttpResponseMessage?> Observe(HttpRequestMessage _, CancellationToken __) { requests++; return Task.FromResult<HttpResponseMessage?>(null); }
        using var rig = new PosterMatchingContractTestRig(Scenario(comic), booksResponse: Observe, comicResponse: Observe);
        var id = Create(rig, comic);
        rig.Repository.SetManualMatch(id, comic ? "comicvine" : "googlebooks", comic ? "comic" : "book", "manual-choice");
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal("manual-choice", rig.GetReleaseForPoster(id)!.ManualMatchId);
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationFailureRollsBackCompletely(bool comic)
    {
        using var rig = new PosterMatchingContractTestRig(Scenario(comic));
        var id = Create(rig, comic);
        using (var conn = rig.Database.Open()) conn.Execute("CREATE TRIGGER fail_print BEFORE UPDATE OF ext_title ON releases WHEN NEW.ext_title IS NOT NULL BEGIN SELECT RAISE(ABORT,'rollback'); END;");
        Assert.False((await rig.FetchAsync(id)).Ok);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Null(row.ExtProviderId); Assert.Null(row.ExtTitle); Assert.Null(row.PosterFile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRevisionDuringDownloadRejectsStalePublication(bool comic)
    {
        PosterMatchingContractTestRig? active = null; long id = 0;
        Task<HttpResponseMessage?> Observe(HttpRequestMessage request, CancellationToken _) {
            if (request.RequestUri!.AbsolutePath.EndsWith(".jpg")) active!.Repository.SetManualMatch(id, comic ? "comicvine" : "googlebooks", comic ? "comic" : "book", "chosen");
            return Task.FromResult<HttpResponseMessage?>(null);
        }
        using var rig = new PosterMatchingContractTestRig(Scenario(comic), booksResponse: Observe, comicResponse: Observe);
        active = rig; id = Create(rig, comic);
        Assert.Equal(409, (await rig.FetchAsync(id)).StatusCode);
        Assert.Equal("chosen", rig.GetReleaseForPoster(id)!.ManualMatchId);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
    }
    [Fact]
    public async Task OpenLibraryFallbackUsesEditionIdInsteadOfIsbnOrWorkId()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbMiss, openLibraryResponse: (request, _) =>
            Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host == "openlibrary.org" ? Json("""
              {"key":"/books/OL1M","title":"Clean Architecture","isbn_13":["9780134494166"],"publish_date":"2017","covers":[1]}
              """) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(PosterTestImage.Create()) }));
        var id = Create(rig, false);
        Assert.True((await rig.FetchAsync(id)).Ok);
        Assert.Equal("openlibrary", rig.GetReleaseForPoster(id)!.ExtProvider);
        Assert.Equal("books/OL1M", rig.GetReleaseForPoster(id)!.ExtProviderId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringDownloadDoesNotPublish(bool comic)
    {
        using var cancel = new CancellationTokenSource();
        async Task<HttpResponseMessage?> Observe(HttpRequestMessage request, CancellationToken token) {
            if (request.RequestUri!.AbsolutePath.EndsWith(".jpg")) { cancel.Cancel(); await Task.Delay(Timeout.Infinite, token); }
            return null;
        }
        using var rig = new PosterMatchingContractTestRig(Scenario(comic), booksResponse: Observe, comicResponse: Observe);
        var id = Create(rig, comic);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.FetchAsync(id, cancel.Token));
        Assert.Null(rig.GetReleaseForPoster(id)!.ExtProviderId);
        Assert.Null(rig.GetReleaseForPoster(id)!.PosterFile);
    }

    [Fact]
    public void BookVolumeNumberIsPreservedSeparatelyFromTheTitle()
    {
        using var data = JsonDocument.Parse("""
          {"totalItems":1,"items":[{"id":"book-900","volumeInfo":{"title":"Clean Architecture Tome 2","authors":["Robert Martin"]}}]}
          """);
        var rows = PrintCandidateAdapter.Google(data.RootElement);
        var context = PrintCandidateAdapter.Context("Robert Martin - Clean Architecture Tome 2", UnifiedCategory.Book, "Clean Architecture", "Robert Martin", null);
        Assert.Equal("2", rows.Single().Candidate.Print!.Number);
        Assert.Equal(CandidateDecision.Accept, SearchCandidateRanker.Rank(context, rows.Select(r => r.Candidate).ToArray()).Decision);
        Assert.Equal(CandidateDecision.Reject, SearchCandidateRanker.Rank(context with { PrintNumber = "3" }, rows.Select(r => r.Candidate).ToArray()).Decision);
    }

}
