using Feedarr.Api.Models;
using Feedarr.Api.Services.Posters;
using Microsoft.AspNetCore.WebUtilities;

namespace Feedarr.Api.Tests;

public sealed class ParserProviderQueryTests
{
    [Theory]
    [InlineData("Argo.2012.DTS.1080p.BluRay-GRP", "Argo", 2012)]
    [InlineData("Argo.2012.AVC-GRP", "Argo", 2012)]
    [InlineData("Argo.FRENCH.1080p.WEB-DL.x264-GRP", "Argo", null)]
    [InlineData("The.French.Dispatch.2021.WEB-DL.x264-GRP", "The French Dispatch", 2021)]
    [InlineData("Blade.Runner.2049.1080p", "Blade Runner 2049", null)]
    public async Task IngestedMovie_UsesParsedTitleAndYearInJobsAndTmdbRequests(string raw, string title, int? year)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.MovieTmdbHit);
        var id = rig.IngestRelease(raw, UnifiedCategory.Film);
        AssertJobs(rig, id, title, year, UnifiedCategory.Film);

        await rig.FetchAsync(id);

        var requests = rig.TmdbSearchRequests.Where(uri => uri.AbsolutePath.EndsWith("/search/movie")).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests, uri => Assert.Equal(title, QueryHelpers.ParseQuery(uri.Query)["query"].ToString()));
        if (year.HasValue)
            Assert.Contains(requests, uri => QueryHelpers.ParseQuery(uri.Query)["year"].ToString() == year.Value.ToString());
        else
            Assert.All(requests, uri => Assert.False(QueryHelpers.ParseQuery(uri.Query).ContainsKey("year")));
    }

    [Theory]
    [InlineData("Portal.2.Update.v1.2.3-CODEX", "Portal 2")]
    [InlineData("Cyberpunk.2077.Update.v2.1-CODEX", "Cyberpunk 2077")]
    [InlineData("F1.2026.Update.v1.2-CODEX", "F1 2026")]
    [InlineData("The.Linux.Tycoon.v1.2-CODEX", "The Linux Tycoon")]
    [InlineData("The.Update.Story.v1.2-CODEX", "The Update Story")]
    public async Task IngestedGame_PreservesTitleWordsAndNumbersInIgdbRequests(string raw, string title)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameIgdbMiss);
        var id = rig.IngestRelease(raw, UnifiedCategory.JeuWindows);
        AssertJobs(rig, id, title, null, UnifiedCategory.JeuWindows);

        await rig.FetchAsync(id);

        Assert.NotEmpty(rig.IgdbSearchRequests);
        Assert.All(rig.IgdbSearchRequests, body => Assert.Contains($"search \"{title}\";", body));
        Assert.All(rig.IgdbSearchRequests, body => Assert.DoesNotContain("first_release_date >=", body));
    }

    private static void AssertJobs(PosterMatchingContractTestRig rig, long id, string title, int? year, UnifiedCategory category)
    {
        var factory = new PosterFetchJobFactory(rig.Repository);
        var job = factory.Create(id, false);
        var seed = Assert.Single(rig.Repository.GetNewPosterJobSeeds(1, 0), seed => seed.Id == id);
        var seedJob = factory.CreateFromSeed(seed, false);
        Assert.NotNull(job);
        Assert.NotNull(seedJob);
        Assert.Equal(title, job.Title);
        Assert.Equal(year, job.Year);
        Assert.Equal(category, job.Category);
        Assert.Equal(job, seedJob);
    }

    [Theory]
    [InlineData("Cyberpunk.2077.Update.v2.1-CODEX", "Cyberpunk 2077")]
    [InlineData("F1.2026.Update.v1.2-CODEX", "F1 2026")]
    public async Task RawgFallback_UsesSamePreservedGameIdentity(string raw, string title)
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.GameRawgHit);
        var id = rig.IngestRelease(raw, UnifiedCategory.JeuWindows);
        await rig.FetchAsync(id);

        Assert.NotEmpty(rig.RawgSearchRequests);
        Assert.All(rig.RawgSearchRequests, uri => Assert.Equal(title, QueryHelpers.ParseQuery(uri.Query)["search"].ToString()));
    }
}
