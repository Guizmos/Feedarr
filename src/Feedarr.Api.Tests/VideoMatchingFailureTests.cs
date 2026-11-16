using System.Net;
using System.Text.Json;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Tests;

public sealed class VideoMatchingFailureTests
{
    [Theory]
    [InlineData(429, "provider_transient", 503)]
    [InlineData(500, "provider_transient", 503)]
    [InlineData(401, "provider_permanent", 422)]
    public void HttpFailuresHaveBoundedRetryClassification(int status, string category, int result)
    {
        var failure = VideoMatchingFailure.Classify(new HttpRequestException("secret-token", null, (HttpStatusCode)status), "collection");
        Assert.Equal(category, failure.Category);
        Assert.Equal(result, failure.Status);
        Assert.DoesNotContain("secret", failure.ToString());
    }

    [Fact]
    public void TimeoutParsingBudgetPublicationAndCancellationAreDistinct()
    {
        Assert.Equal("provider_timeout", VideoMatchingFailure.Classify(new TimeoutException(), "collection").Category);
        Assert.Equal("provider_parse", VideoMatchingFailure.Classify(new JsonException("secret"), "collection").Category);
        Assert.Equal("coverage_budget", VideoMatchingFailure.Classify(new InvalidDataException("tv_http_budget"), "enrichment").Category);
        Assert.Equal("publication_error", VideoMatchingFailure.Classify(new IOException("secret"), "publication").Category);
        Assert.Equal("cancelled_or_timeout", VideoMatchingFailure.Classify(new OperationCanceledException(), "collection").Category);
        Assert.Equal(422, VideoMatchingFailure.Classify(new InvalidOperationException(), "ranking").Status);
    }
    [Fact]
    public async Task ProviderParseFailurePersistsSafeReasonWithoutPartialIdentity()
    {
        using var rig = new PosterMatchingContractTestRig(PosterContractScenario.SeriesTvmazeMissTmdb,
            completeTmdbMetadata: true, tmdbResponse: (request, _) => throw new JsonException("https://private/?apikey=secret"));
        var id = rig.IngestRelease("Show.Tmdb.Hit.S01.1080p.WEB.x264-GRP", Feedarr.Api.Models.UnifiedCategory.Serie);
        var result = await rig.FetchAsync(id);
        Assert.False(result.Ok);
        Assert.Equal(422, result.StatusCode);
        var row = rig.GetReleaseForPoster(id)!;
        Assert.Null(row.TmdbId);
        Assert.Null(row.PosterFile);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(result.Body));
    }
}
