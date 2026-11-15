using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;
using Feedarr.Api.Services.Tmdb;

namespace Feedarr.Api.Services.Matching;

internal sealed record AudioEvidence(SearchIdentity Identity, string? Artist, string? Kind, string? Edition,
    SearchIdentity? ReleaseGroup = null, IReadOnlyList<SearchIdentity>? Artists = null);
internal sealed record AudioObservation(ProviderSearchCandidate Candidate, string? Image, TmdbClient.DetailsResult Details);

// Provider facts only. A MusicBrainz release, release-group and artist are different identities.
internal static class AudioCandidateAdapter
{
    internal static string? Edition(string raw)
    {
        var words = raw.Replace('.', ' ').Replace('_', ' ');
        var found = Regex.Matches(words, @"(?i)\b(remaster(?:ed)?|reissue|deluxe|anniversary|expanded)\b").Select(m => m.Value.ToLowerInvariant().Replace("remastered", "remaster")).Distinct().Order().ToArray();
        return found.Length == 0 ? null : string.Join("|", found);
    }
    internal static string? Kind(string raw)
    {
        var match = Regex.Match(raw, @"(?i)[\[(](album|track|single|ep|compilation)[\])]");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }
    internal static string Scope(string raw)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Audio);
        return TitleNormalizer.NormalizeTitleStrict(parsed.TitleClean ?? "") + "|" + parsed.Year + "|" + Kind(raw) + "|" + Edition(raw);
    }
    internal static CandidateRankingContext Context(string raw, string? artist, string title)
    {
        var parsed = new TitleParser().Parse(raw, UnifiedCategory.Audio);
        title = Regex.Replace(title, @"(?i)[\[(](album|track|single|ep|compilation)[\])]", " ").Trim();
        return new(new[] { new SearchCandidate(title, parsed.Year, parsed.Year.HasValue ? CandidateYearEvidence.Parsed : CandidateYearEvidence.None, SearchCandidateOrigin.Primary) },
            UnifiedCategory.Audio, SearchMediaType.Audio, AudioArtist: artist, AudioKind: Kind(raw), AudioEdition: Edition(raw));
    }
    internal static IReadOnlyList<AudioObservation> FromAudioDb(JsonElement root, string kind)
    {
        if (kind is not ("album" or "track") || !root.TryGetProperty(kind, out var data) || data.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            throw new InvalidDataException("audio_search_coverage_unavailable");
        if (data.ValueKind == JsonValueKind.Null) return Array.Empty<AudioObservation>();
        if (data.GetArrayLength() > 25) throw new InvalidDataException("audio_candidate_budget");
        return data.EnumerateArray().Select(item => {
            var id = Text(item, kind == "track" ? "idTrack" : "idAlbum");
            if (!long.TryParse(id, out var number) || number <= 0) throw new InvalidDataException("audio_identity_invalid");
            var identity = new SearchIdentity("theaudiodb", SearchMediaType.Audio, kind + ":" + number.ToString(CultureInfo.InvariantCulture));
            var title = Text(item, kind == "track" ? "strTrack" : "strAlbum") ?? "";
            var artist = Text(item, "strArtist");
            var format = Text(item, "strReleaseFormat")?.ToLowerInvariant();
            var proof = new AudioEvidence(identity, artist, kind == "track" ? "track" : format, Edition(title));
            var date = Text(item, "intYearReleased");
            var details = new TmdbClient.DetailsResult(title, Text(item, "strDescriptionEN"), null, Text(item, "strGenre"), date, null, null, null, null, null, artist);
            return new AudioObservation(new(identity, title, Year(date), Category: UnifiedCategory.Audio, Audio: proof),
                Text(item, kind == "track" ? "strTrackThumb" : "strAlbumThumb"), details);
        }).ToArray();
    }
    internal static IReadOnlyList<AudioObservation> FromMusicBrainz(JsonElement root)
    {
        if (!root.TryGetProperty("releases", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 25 ||
            !root.TryGetProperty("count", out var count) || !count.TryGetInt32(out var total) || total != rows.GetArrayLength())
            throw new InvalidDataException("audio_search_coverage_unavailable");
        return rows.EnumerateArray().Select(item => {
            var id = Text(item, "id");
            if (!Guid.TryParse(id, out _)) throw new InvalidDataException("audio_release_identity_invalid");
            var identity = new SearchIdentity("musicbrainz", SearchMediaType.Audio, id!);
            var title = Text(item, "title") ?? "";
            string? artist = null;
            var artistIds = new List<SearchIdentity>();
            if (item.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array)
                artist = string.Concat(credits.EnumerateArray().Select(c => (Text(c, "name") ?? (c.TryGetProperty("artist", out var a) ? Text(a, "name") : null)) + Text(c, "joinphrase")));
            if (item.TryGetProperty("artist-credit", out credits) && credits.ValueKind == JsonValueKind.Array)
                foreach (var credit in credits.EnumerateArray())
                    if (credit.TryGetProperty("artist", out var entity) && Guid.TryParse(Text(entity, "id"), out var artistId))
                        artistIds.Add(new("musicbrainz-artist", SearchMediaType.Audio, artistId.ToString()));
            string? kind = null; SearchIdentity? group = null;
            if (item.TryGetProperty("release-group", out var releaseGroup))
            {
                if (Guid.TryParse(Text(releaseGroup, "id"), out var groupId)) group = new("musicbrainz-release-group", SearchMediaType.Audio, groupId.ToString());
                kind = Text(releaseGroup, "primary-type")?.ToLowerInvariant();
                if (releaseGroup.TryGetProperty("secondary-types", out var types) && types.ValueKind == JsonValueKind.Array && types.GetArrayLength() > 0)
                    kind = "special"; // Compilation/live/remix is not evidence of an ordinary studio album.
            }
            var proof = new AudioEvidence(identity, artist, kind, Edition(title + " " + Text(item, "disambiguation")), group, artistIds);
            var date = Text(item, "date");
            return new AudioObservation(new(identity, title, Year(date), Category: UnifiedCategory.Audio, Audio: proof), null,
                new(title, null, null, null, date, null, null, null, null, null, artist));
        }).ToArray();
    }
    private static int? Year(string? raw) => raw is { Length: >= 4 } && int.TryParse(raw.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
    private static string? Text(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
