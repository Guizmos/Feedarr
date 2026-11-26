using System.Reflection;
using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;

namespace Feedarr.Api.Tests;

// Offline calibration artifact only. No provider client, store, service or budget changes.
public sealed class MatchingP7CalibrationTests
{
    private sealed record Case(string Name, string Corpus, CandidateRankingContext Context,
        ProviderSearchCandidate[] Pool, string Label, string? ExpectedId);
    private static CandidateRankingContext Context(string title, int? year = 2020, UnifiedCategory category = UnifiedCategory.Film,
        SearchMediaType type = SearchMediaType.Movie) => new(new[] { new SearchCandidate(title, year,
            year.HasValue ? CandidateYearEvidence.Parsed : CandidateYearEvidence.None, SearchCandidateOrigin.Primary) }, category, type);
    private static ProviderSearchCandidate Candidate(string title, int? year = 2020, string id = "1", SearchMediaType type = SearchMediaType.Movie)
        => new(new("tmdb", type, id), title, year);

    [Fact]
    public async Task CompareWithoutChangingRuntimePolicy()
    {
        var cases = new List<Case>();
        foreach (var row in SearchCandidateRankerTests.RepresentativeCases())
        {
            var context = (CandidateRankingContext)row[1]; var pool = (ProviderSearchCandidate[])row[2];
            var expected = (CandidateDecision)row[3];
            cases.Add(new((string)row[0], "existing-representative", context, pool,
                expected == CandidateDecision.Accept ? "synthetic-positive" : "synthetic-abstention", expected == CandidateDecision.Accept ? pool[0].Identity.Id : null));
        }
        var method = typeof(TitleTypographyCorpusTests).GetMethod(nameof(TitleTypographyCorpusTests.CurrentBoundaryPolicyIsExplicit))!;
        foreach (var row in method.GetCustomAttributes<InlineDataAttribute>())
        {
            var data = row.GetData(method).Single(); var query = (string)data[0]; var title = (string)data[1]; var accepts = (bool)data[2];
            cases.Add(new(query + "/" + title, "existing-typography", Context(query, 2009), new[] { Candidate(title, 2009) },
                accepts ? "synthetic-positive" : "synthetic-abstention", accepts ? "1" : null));
        }
        foreach (var row in MatchingMultilingualTests.BetaProposals())
        {
            var title = (string)row[0]; var year = (int)row[1]; var id = row[2].ToString()!;
            var c = Candidate((string)row[3], year, id);
            c = c with { OfficialTitles = new[] { new OfficialProviderTitle(title, c.Identity, ProviderTitleKind.Localized, ProviderTitleSource.SearchTitle, "fr-FR") } };
            cases.Add(new(title, "existing-localized", Context(title, year), new[] { c }, "synthetic-positive", id));
        }
        foreach (var row in CompoundTitleShadowTests.Cases())
        {
            var year = (int)row[1]; var id = row[2].ToString()!; var c = Candidate((string)row[4], year, id);
            c = c with { OfficialTitles = new[] {
                new OfficialProviderTitle((string)row[3], c.Identity, ProviderTitleKind.Localized, ProviderTitleSource.SearchTitle, "fr-FR"),
                new OfficialProviderTitle((string)row[4], c.Identity, ProviderTitleKind.Original, ProviderTitleSource.SearchOriginalTitle) } };
            var queries = SearchCandidateGenerator.Generate(new((string)row[0] + ".BluRay", UnifiedCategory.Film));
            cases.Add(new((string)row[0], "existing-compound", new(queries, UnifiedCategory.Film, SearchMediaType.Movie), new[] { c }, "synthetic-positive", id));
        }
        var variants = typeof(MatchingP6CTitleTests).GetMethod(nameof(MatchingP6CTitleTests.OfficialVideoVariantsRequireReliableYear))!;
        foreach (var attribute in variants.GetCustomAttributes<InlineDataAttribute>())
        {
            var row = attribute.GetData(variants).Single(); var raw = (string)row[0]; var title = (string)row[1]; var year = (int)row[2];
            var c = Candidate(title, year) with { OfficialTitles = new[] { new OfficialProviderTitle(title, new("tmdb", SearchMediaType.Movie, "1"), ProviderTitleKind.Original, ProviderTitleSource.SearchOriginalTitle) } };
            cases.Add(new(raw, "existing-p6c", new(SearchCandidateGenerator.Generate(new(raw, UnifiedCategory.Film)), UnifiedCategory.Film, SearchMediaType.Movie), new[] { c }, "synthetic-positive", "1"));
        }
        foreach (var row in MatchingP6CTitleTests.Duos())
        {
            var c = Candidate("Les Duos impossibles de Jérémy Ferrari 11e édition", 2026, "1814493");
            c = c with { OfficialTitles = new[] { new OfficialProviderTitle(c.Title, c.Identity, ProviderTitleKind.Original, ProviderTitleSource.SearchOriginalTitle) } };
            cases.Add(new(row[0].ToString()!, "existing-p6c-event", new(SearchCandidateGenerator.Generate(new((string)row[1], UnifiedCategory.Spectacle)), UnifiedCategory.Spectacle, SearchMediaType.Movie), new[] { c }, "synthetic-positive", c.Identity.Id));
        }
        // Existing TV tests: missing structure and conflicting episode are abstentions,
        // not evidence that a generic high-confidence title should be accepted.
        var tvContext = Context("FBI", null, UnifiedCategory.Serie, SearchMediaType.Series) with { Season = 8, Episode = 5 };
        var tv = Candidate("FBI", 2018, type: SearchMediaType.Series) with { Season = 8, Episode = 5 };
        cases.Add(new("FBI missing proof", "existing-tv", tvContext, new[] { tv }, "synthetic-abstention", null));
        tv = tv with { Television = new(tv.Identity, true, true) };
        cases.Add(new("FBI verified structure", "existing-tv", tvContext, new[] { tv }, "synthetic-positive", "1"));
        cases.Add(new("FBI wrong episode", "existing-tv", tvContext, new[] { tv with { Episode = 6 } }, "synthetic-abstention", null));
        cases.Add(new("FBI two verified identities", "existing-tv", tvContext, new[] { tv, tv with { Identity = tv.Identity with { Id = "2" }, Television = new(tv.Identity with { Id = "2" }, true, true) } }, "synthetic-abstention", null));
        foreach (var category in new[] { UnifiedCategory.Film, UnifiedCategory.Serie, UnifiedCategory.Spectacle, UnifiedCategory.JeuWindows })
        {
            var type = category == UnifiedCategory.JeuWindows ? SearchMediaType.Game : category == UnifiedCategory.Serie ? SearchMediaType.Series : SearchMediaType.Movie;
            var context = Context("Identity", category: category, type: type); var candidate = Candidate("Identity", type: type);
            foreach (var state in new[] { "manual", "established", "locked" })
                cases.Add(new(category + " " + state, "existing-preservation-rule", context with {
                    IsLocked = state == "locked", HasManualCorrection = state == "manual", EstablishedIdentity = state == "established" ? candidate.Identity : null }, new[] { candidate }, "synthetic-abstention", null));
        }
        // Observed audit search fixtures are not complete transport captures and
        // SelectedId is a proposal, not ground truth. Never score their precision.
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Matching", "exact-movie-year-audit.json")));
        foreach (var group in new[] { "Cases", "NegativeCases" })
        foreach (var entry in audit.RootElement.GetProperty(group).EnumerateArray())
        {
            var category = Enum.Parse<UnifiedCategory>(entry.GetProperty("Category").GetString()!);
            cases.Add(new(entry.GetProperty("ReleaseId").ToString(), "observed-audit-subset-unknown-coverage",
                new(SearchCandidateGenerator.Generate(new(entry.GetProperty("RawTitle").GetString(), category)), category, SearchMediaType.Movie),
                entry.GetProperty("Candidates").Deserialize<ProviderSearchCandidate[]>()!, "unknown", null));
        }
        foreach (var methodName in new[] { nameof(MatchingP6AExactTests.SameYearExactHomonymsNeverGetArbitraryPriority),
            nameof(MatchingP6AExactTests.ANeighborsEarlierOriginalPremiereIsAnIdentityCompetitor) })
        {
            var negativeMethod = typeof(MatchingP6AExactTests).GetMethod(methodName)!;
            foreach (var attribute in negativeMethod.GetCustomAttributes<InlineDataAttribute>())
            {
                var data = attribute.GetData(negativeMethod).Single(); var title = (string)data[0]; var year = (int)data[1];
                ProviderSearchCandidate Film(string id, int date) { var c = Candidate(title, date, id); return c with { OfficialTitles = new[] {
                    new OfficialProviderTitle(title, c.Identity, ProviderTitleKind.Original, ProviderTitleSource.SearchOriginalTitle),
                    new OfficialProviderTitle(title, c.Identity, ProviderTitleKind.Localized, ProviderTitleSource.SearchTitle, "fr-FR") } }; }
                cases.Add(new(title, "existing-p6a-negative", Context(title, year), new[] { Film((string)data[2], year),
                    Film((string)data[3], methodName.Contains("Neighbors") ? year + 1 : year) }, "synthetic-abstention", null));
            }
        }
        var profiles = new[] { ("Actuel", .90, .92, .08), ("Leger", .88, .91, .06), ("Equilibre", .85, .90, .05), ("Permissif", .82, .88, .04) };
        var output = new List<object>();
        foreach (var c in cases)
        foreach (var (name, similarity, acceptance, margin) in profiles)
        foreach (var relax in new[] { false, true })
        {
            var result = SearchCandidateRanker.Rank(c.Context, c.Pool, new(similarity, acceptance, margin, RequireExactTitleForAcceptance: !relax));
            string? pipelineDecision = null, pipelineOutcome = null;
            if (c.Corpus == "existing-p6a-negative")
            {
                var proof = typeof(MatchingP6AExactTests).GetMethod("Proof", BindingFlags.NonPublic | BindingFlags.Static)!;
                var collection = new VideoSearchCollection([], null, new(2, 6, 6, c.Pool.Length), 6, "targeted_year");
                var year = c.Context.Queries[0].Year!.Value;
                var session = new MovieExactYearPriority(c.Context, $"{c.Name}.{year}.1080p.WEB.H264", collection, c.Pool,
                    (id, _) => Task.FromResult<JsonElement?>((JsonElement)proof.Invoke(null, new object?[] { c.Pool.Single(p => p.Identity.Id == id), year, null, 5, null, null })!),
                    (_, _) => throw new InvalidOperationException("No provider alias call during calibration"));
                var final = await session.ApplyAsync(c.Pool, result, default);
                pipelineDecision = final.Decision.ToString(); pipelineOutcome = session.Outcome;
            }
            output.Add(new { c.Name, c.Corpus, Category = c.Context.Category.ToString(), c.Label, c.ExpectedId, Profile = name,
                RemoveExactVeto = relax, Decision = result.Decision.ToString(), SelectedId = result.SelectedIdentity?.Id,
                result.PreserveExisting, PipelineDecision = pipelineDecision, PipelineOutcome = pipelineOutcome, Candidates = result.Candidates });
            if (c.Context.IsLocked || c.Context.HasManualCorrection || c.Context.EstablishedIdentity is not null) Assert.True(result.PreserveExisting);
            if (name == "Actuel" && !relax && c.Label != "unknown") Assert.Equal(c.Label == "synthetic-positive", result.Decision == CandidateDecision.Accept);
        }
        Directory.CreateDirectory("/tmp/feedarr-p7");
        File.WriteAllText("/tmp/feedarr-p7/corpus-results.json", JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(.90, new CandidateRankingPolicy().MinimumTitleSimilarity);
        Assert.Equal(.92, new CandidateRankingPolicy().AcceptConfidence);
        Assert.Equal(.08, new CandidateRankingPolicy().MinimumConfidenceGap);
        Assert.True(new CandidateRankingPolicy().RequireExactTitleForAcceptance);
    }
    [Fact]
    public void CompareVerifiedCapturesOnlyWhenExplicitlyProvided()
    {
        var path = Environment.GetEnvironmentVariable("FEEDARR_P7_CAPTURED_REPLAY");
        var selectionPath = Environment.GetEnvironmentVariable("FEEDARR_P7_CAPTURED_SELECTION");
        if (path is null || selectionPath is null) return;
        using var replay = JsonDocument.Parse(File.ReadAllText(path));
        using var selection = JsonDocument.Parse(File.ReadAllText(selectionPath));
        Assert.True(replay.RootElement.GetProperty("completed").GetBoolean());
        Assert.Equal(0, replay.RootElement.GetProperty("networkRequests").GetInt32());
        var labels = selection.RootElement.GetProperty("labels");
        var outputs = new List<object>();
        foreach (var row in replay.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (row.GetProperty("benchmarkStatus").ValueKind != JsonValueKind.Null ||
                !row.TryGetProperty("entry", out var entry) || entry.ValueKind == JsonValueKind.Null ||
                !entry.TryGetProperty("rankingInput", out var payload) || payload.ValueKind == JsonValueKind.Null ||
                entry.GetProperty("coverage").GetString() != "complete" || entry.GetProperty("error").GetBoolean()) continue;
            var id = row.GetProperty("id").ToString();
            var context = payload.GetProperty("Context").Deserialize<CandidateRankingContext>()!;
            var candidates = payload.GetProperty("Candidates").Deserialize<ProviderSearchCandidate[]>()!;
            var label = labels.GetProperty(id);
            Assert.True(label.GetProperty("verified").GetBoolean());
            var correct = label.GetProperty("correctIdentities").EnumerateArray().Select(v => v.GetString()).ToArray();
            foreach (var (name, similarity, acceptance, margin) in new[] { ("Actuel", .90, .92, .08), ("Leger", .88, .91, .06), ("Equilibre", .85, .90, .05), ("Permissif", .82, .88, .04) })
            foreach (var relaxed in new[] { false, true })
            {
                var result = SearchCandidateRanker.Rank(context, candidates, new(similarity, acceptance, margin, RequireExactTitleForAcceptance: !relaxed));
                var identity = result.SelectedIdentity is { } selected ? $"{selected.Provider}:{(selected.MediaType == SearchMediaType.Series ? "tv" : selected.MediaType.ToString().ToLowerInvariant())}:{selected.Id}" : null;
                outputs.Add(new { Id = id, Category = context.Category.ToString(), Profile = name, RemoveExactVeto = relaxed,
                    Decision = result.Decision.ToString(), Identity = identity,
                    Correct = identity is not null ? (bool?)correct.Contains(identity) : null,
                    Candidates = result.Candidates });
            }
        }
        File.WriteAllText("/tmp/feedarr-p7/captured-profile-results.json", JsonSerializer.Serialize(outputs, new JsonSerializerOptions { WriteIndented = true }));
    }

}
