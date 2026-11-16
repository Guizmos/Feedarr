using System.Globalization;
using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Services.Matching;

internal sealed record ProviderEpisodeIdentity(SearchIdentity Series, int Id, int Season, int Number);
internal sealed record EpisodeAirDate(SearchIdentity Series, string Date);

// Evidence is scoped to one provider identity, never inferred from the query.
internal sealed record TelevisionEvidence(SearchIdentity Identity, bool? SeasonExists, bool? EpisodeExists,
    string? AirDate = null, SearchIdentity? CorroboratingIdentity = null,
    ProviderEpisodeIdentity? EpisodeIdentity = null, IReadOnlyList<EpisodeAirDate>? AirDates = null, IReadOnlyList<ProviderEpisodeIdentity>? EpisodeIdentities = null);

internal static class TelevisionCandidates
{
    internal static async Task<ProviderSearchCandidate[]> EnrichAsync(
        IReadOnlyList<SearchCandidate> queries, UnifiedCategory category, string rawTitle,
        IReadOnlyList<ProviderSearchCandidate> candidates,
        Func<string, string, CancellationToken, Task<JsonElement?>> get, bool tvmaze, CancellationToken ct)
    {
        var parsed = new TitleParser().Parse(rawTitle, category);
        var rows = candidates.ToList();
        // One TVMaze search per distinct title; never choose its first hit implicitly.
        if (tvmaze)
        foreach (var query in queries.Select(q => q.Title).Distinct(StringComparer.Ordinal))
        {
            var response = await get("tvmaze", "search/shows?q=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
            if (response is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() >= 50)
                throw new InvalidDataException("tvmaze_search_coverage_unavailable");
            foreach (var item in array.EnumerateArray())
            {
                if (!item.TryGetProperty("show", out var show) || Number(show, "id") is not > 0 || Text(show, "name") is not { Length: > 0 } name)
                    throw new InvalidDataException("tvmaze_search_invalid");
                var identity = new SearchIdentity("tvmaze", SearchMediaType.Series, Number(show, "id")!.Value.ToString(CultureInfo.InvariantCulture));
                rows.Add(new(identity, name, Year(Text(show, "premiered")), OfficialTitles: new[] {
                    new OfficialProviderTitle(name, identity, ProviderTitleKind.ResponseTitle, ProviderTitleSource.SearchTitle) }));
            }
        }
        if (rows.GroupBy(r => r.Identity).Any(g => g.Select(r => r.Year).Where(y => y.HasValue).Distinct().Count() > 1))
            throw new InvalidDataException("tv_conflicting_provider_records");
        rows = rows.DistinctBy(r => r.Identity).ToList();
        if (rows.Count > 100) throw new InvalidDataException("tv_candidate_budget");
        // Enrich every plausible competitor. A hard cap abstains instead of hiding rivals.
        var plausible = rows.Where(r => SearchCandidateRanker.Rank(new(queries, category, SearchMediaType.Series), new[] { r })
            .Candidates.Any(c => !c.Contradictions.Any(x => x.IsStrong && x.Code is not ("parsed-year-mismatch" or "title-number-mismatch")))) .ToArray();
        if (plausible.Length > 6) throw new InvalidDataException("tv_evidence_budget");
        var verified = new Dictionary<SearchIdentity, JsonElement>();
        foreach (var row in plausible)
        {
            var path = row.Identity.Provider == "tmdb" ? "tv/" + row.Identity.Id + "?append_to_response=external_ids,alternative_titles" : "shows/" + row.Identity.Id;
            var value = await get(row.Identity.Provider, path, ct).ConfigureAwait(false);
            if (value is not { } details || Number(details, "id")?.ToString(CultureInfo.InvariantCulture) != row.Identity.Id)
                throw new InvalidDataException("tv_details_identity_unavailable");
            var year = Year(Text(details, row.Identity.Provider == "tmdb" ? "first_air_date" : "premiered"));
            if (row.Year.HasValue && year.HasValue && row.Year != year) throw new InvalidDataException("tv_provider_year_conflict");
            verified.Add(row.Identity, details);
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!verified.TryGetValue(row.Identity, out var details)) continue;
            bool? seasonExists = null, episodeExists = null; string? airDate = null;
            ProviderEpisodeIdentity? episodeIdentity = null;
            var titles = (row.OfficialTitles ?? Array.Empty<OfficialProviderTitle>()).ToList();
            if (row.Identity.Provider == "tmdb")
            {
                if (details.TryGetProperty("alternative_titles", out var alternatives) && alternatives.TryGetProperty("results", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var alternative in list.EnumerateArray())
                        if (Text(alternative, "title") is { Length: > 0 } title)
                            titles.Add(new(title, row.Identity, ProviderTitleKind.Alternative, ProviderTitleSource.AlternativeTitlesEndpoint));
                if (parsed.Season.HasValue && details.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
                {
                    var matches = seasons.EnumerateArray().Where(s => Number(s, "season_number") == parsed.Season).ToArray();
                    // An absent/planned season is not proof of non-existence in an incomplete catalogue.
                    if (matches.Length == 1 && Number(matches[0], "episode_count") is > 0) seasonExists = true;
                }
                if (parsed.Season.HasValue && parsed.Episode.HasValue)
                {
                    var episode = await get("tmdb", $"tv/{row.Identity.Id}/season/{parsed.Season}/episode/{parsed.Episode}", ct).ConfigureAwait(false);
                    if (episode is { } e && Number(e, "id") is > 0 && Number(e, "season_number") == parsed.Season && Number(e, "episode_number") == parsed.Episode)
                    {
                        if (Number(e, "show_id") is > 0 && Number(e, "show_id")?.ToString(CultureInfo.InvariantCulture) != row.Identity.Id)
                            throw new InvalidDataException("tv_episode_identity_conflict");
                        seasonExists = true; episodeExists = true; airDate = Text(e, "air_date");
                        // A requested URL alone is not independent proof of the parent series.
                        if (Number(e, "show_id")?.ToString(CultureInfo.InvariantCulture) == row.Identity.Id)
                            episodeIdentity = new(row.Identity, Number(e, "id")!.Value, parsed.Season.Value, parsed.Episode.Value);
                    }
                    // 404 remains unknown; providers may not yet list a real episode.
                }
            }
            else if (parsed.Season.HasValue || parsed.AirDate is not null)
            {
                var path = parsed.Season.HasValue ? $"shows/{row.Identity.Id}/episodes" : $"shows/{row.Identity.Id}/episodesbydate?date={Uri.EscapeDataString(parsed.AirDate!)}";
                var episodes = await get("tvmaze", path, ct).ConfigureAwait(false);
                if (episodes is { ValueKind: JsonValueKind.Array } items)
                {
                    var matches = items.EnumerateArray().Where(e => (!parsed.Season.HasValue || Number(e, "season") == parsed.Season) &&
                        (!parsed.Episode.HasValue || Number(e, "number") == parsed.Episode) && Number(e, "id") is > 0 &&
                        e.TryGetProperty("_links", out var links) && links.TryGetProperty("show", out var link) &&
                        Text(link, "href") == "https://api.tvmaze.com/shows/" + row.Identity.Id).ToArray();
                    if (matches.Length > 0)
                    { seasonExists = parsed.Season.HasValue ? true : null; episodeExists = parsed.Episode.HasValue ? true : null;
                        airDate = matches.Select(e => Text(e, "airdate")).FirstOrDefault(d => d == parsed.AirDate) ?? (matches.Length == 1 ? Text(matches[0], "airdate") : null); }
                    if (matches.Length == 1 && parsed.Season.HasValue && parsed.Episode.HasValue)
                        episodeIdentity = new(row.Identity, Number(matches[0], "id")!.Value, parsed.Season.Value, parsed.Episode.Value);
                }
            }
            rows[i] = row with { OfficialTitles = titles, Season = seasonExists == true ? parsed.Season : null,
                Episode = episodeExists == true ? parsed.Episode : null, Television = new(row.Identity, seasonExists, episodeExists, airDate, EpisodeIdentity: episodeIdentity,
                    AirDates: airDate is null ? Array.Empty<EpisodeAirDate>() : new[] { new EpisodeAirDate(row.Identity, airDate) }) };
        }
        // Cross-provider merging requires a verified external ID, never a similar name.
        foreach (var tmdb in plausible.Where(r => r.Identity.Provider == "tmdb"))
        {
            var data = verified[tmdb.Identity];
            if (!data.TryGetProperty("external_ids", out var external)) continue;
            foreach (var maze in plausible.Where(r => r.Identity.Provider == "tvmaze").ToArray())
            {
                var show = verified[maze.Identity];
                if (!show.TryGetProperty("externals", out var other)) continue;
                var tvdb = Number(external, "tvdb_id"); var imdb = Text(external, "imdb_id");
                var same = tvdb is > 0 && Number(other, "thetvdb") == tvdb || !string.IsNullOrWhiteSpace(imdb) && Text(other, "imdb") == imdb;
                if (!same) continue;
                if ((tvdb is > 0 && Number(other, "thetvdb") is > 0 && Number(other, "thetvdb") != tvdb) ||
                    (!string.IsNullOrWhiteSpace(imdb) && Text(other, "imdb") is { Length: > 0 } otherImdb && otherImdb != imdb))
                    throw new InvalidDataException("tv_external_identity_conflict");
                var index = rows.FindIndex(r => r.Identity == tmdb.Identity);
                var canonical = rows[index];
                var enrichedMaze = rows.FirstOrDefault(r => r.Identity == maze.Identity)
                    ?? throw new InvalidDataException("tv_multiple_canonical_identities");
                var dates = (canonical.Television?.AirDates ?? Array.Empty<EpisodeAirDate>())
                    .Concat(enrichedMaze.Television?.AirDates ?? Array.Empty<EpisodeAirDate>()).Distinct().ToArray();
                var calendarConflict = dates.Select(d => d.Date).Distinct(StringComparer.Ordinal).Skip(1).Any();
                // Series external IDs do not map episode IDs. Both providers must independently
                // verify the requested numbered episode and its parent before tolerating dates.
                var episode = canonical.Television?.EpisodeIdentity;
                var otherEpisode = enrichedMaze.Television?.EpisodeIdentity;
                if (calendarConflict && (parsed.Season is null || parsed.Episode is null ||
                    episode is null || otherEpisode is null || episode.Series != canonical.Identity ||
                    otherEpisode.Series != enrichedMaze.Identity || episode.Season != otherEpisode.Season || episode.Number != otherEpisode.Number))
                    throw new InvalidDataException("tv_episode_date_conflict");
                rows[index] = canonical with { OfficialTitles = (canonical.OfficialTitles ?? Array.Empty<OfficialProviderTitle>())
                    .Append(new(Text(show, "name")!, tmdb.Identity, ProviderTitleKind.Alternative, ProviderTitleSource.AlternativeTitlesEndpoint)).ToArray(),
                    Season = canonical.Season ?? enrichedMaze.Season, Episode = canonical.Episode ?? enrichedMaze.Episode,
                    Television = canonical.Television is { } proof && enrichedMaze.Television is { } mazeProof
                        ? proof with { AirDate = calendarConflict ? null : proof.AirDate ?? mazeProof.AirDate, AirDates = dates,
                            EpisodeIdentities = new[] { episode, otherEpisode }.Where(e => e is not null).Cast<ProviderEpisodeIdentity>().ToArray(),
                            SeasonExists = proof.SeasonExists ?? mazeProof.SeasonExists, EpisodeExists = proof.EpisodeExists ?? mazeProof.EpisodeExists,
                            CorroboratingIdentity = tmdb.Year.HasValue && enrichedMaze.Year.HasValue &&
                                Math.Abs((long)tmdb.Year.Value - enrichedMaze.Year.Value) <= new CandidateRankingPolicy().ParsedYearTolerance ? maze.Identity : null } : canonical.Television,
                    KnownExternalIdentities = (canonical.KnownExternalIdentities ?? Array.Empty<SearchIdentity>()).Append(maze.Identity).ToArray() };
                rows.RemoveAll(r => r.Identity == maze.Identity);
            }
        }
        return rows.ToArray();
    }
    internal static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var bytes = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 1048576) throw new InvalidDataException("tv_response_budget");
            buffer.Write(bytes, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
    internal static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static int? Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static int? Year(string? date) => date?.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
}
