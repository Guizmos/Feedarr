using System.Text.RegularExpressions;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Titles;

namespace Feedarr.Api.Services.Matching;

internal static class LocalIdentityReuse
{
    internal static string? Scope(ReleaseForPoster release)
    {
        if (!UnifiedCategoryMappings.TryParse(release.UnifiedCategory, out var category)) return null;
        var video = PosterFetchService.ResolveVideoType(release, category);
        return video ?? (category is UnifiedCategory.Animation or UnifiedCategory.Spectacle or UnifiedCategory.Autre ? null : category.ToString());
    }

    internal static bool MetadataCorroborates(ReleaseForPoster release, string? title, string? releaseDate)
    {
        var scope = Scope(release);
        var category = scope == "tv" ? UnifiedCategory.Serie : scope == "movie" ? UnifiedCategory.Film :
            UnifiedCategoryMappings.TryParse(release.UnifiedCategory, out var parsedCategory) ? parsedCategory : UnifiedCategory.Autre;
        var queries = SearchCandidateGenerator.Generate(new(release.Title, category));
        return !string.IsNullOrWhiteSpace(title) && queries.Any(q => q.Origin == SearchCandidateOrigin.Primary &&
            TitleNormalizer.NormalizeTitleStrict(q.Title) == TitleNormalizer.NormalizeTitleStrict(title) &&
            (scope != "movie" || q.Year.HasValue && DateOnly.TryParseExact(releaseDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date) && date.Year == q.Year));
    }

    internal static bool Corroborates(ReleaseForPoster target, ReleaseForPoster donor, bool sameHash)
    {
        var scope = Scope(target);
        if (scope is null || scope != Scope(donor) || string.IsNullOrWhiteSpace(target.Title) || string.IsNullOrWhiteSpace(donor.Title)) return false;
        if (scope == "movie")
        {
            var parsed = new TitleParser().Parse(target.Title, UnifiedCategory.Film);
            var other = new TitleParser().Parse(donor.Title, UnifiedCategory.Film);
            if (!parsed.Year.HasValue || parsed.Year != other.Year ||
                target.Year.HasValue && target.Year != parsed.Year || donor.Year.HasValue && donor.Year != other.Year) return false;
        }
        var exact = string.Equals(target.Title.Trim(), donor.Title.Trim(), StringComparison.OrdinalIgnoreCase);
        // A full release title carries edition/platform/structure tokens. A bare title, even
        // with a year, is not an independently corroborated release fingerprint.
        if (exact && Regex.IsMatch(target.Title, @"(?i)\b(?:720p|1080p|2160p|480p|x264|x265|h264|h265|flac|repack)\b")) return true;
        if (!sameHash) return false;
        // Unknown non-video scopes and game distribution/platform tokens are deliberately
        // exact-only. A torrent hash does not establish which work inside a bundle is meant.
        if (scope is not ("movie" or "tv")) return exact;
        var category = scope == "tv" ? UnifiedCategory.Emission : UnifiedCategory.Film;
        var parser = new TitleParser();
        var a = parser.Parse(target.Title, category);
        var b = parser.Parse(donor.Title, category);
        return TitleNormalizer.NormalizeTitleStrict(a.TitleClean) == TitleNormalizer.NormalizeTitleStrict(b.TitleClean) &&
            !string.IsNullOrWhiteSpace(a.TitleClean) && a.Edition == b.Edition &&
            EditionScope(target.Title) == EditionScope(donor.Title) &&
            a.Year == b.Year && a.Season == b.Season && a.Episode == b.Episode && a.AirDate == b.AirDate &&
            (scope == "tv" ? a.Season.HasValue || a.AirDate is not null : a.Year.HasValue);
    }
    // The general parser intentionally removes some edition words from search text.
    // Keep those raw-title distinctions at the identity reuse boundary.
    private static string EditionScope(string raw) => string.Join("|", Regex.Matches(
        raw.Replace('.', ' ').Replace('_', ' ').Replace('-', ' '),
        @"(?i)\b(?:extended|unrated|uncut|remastered|remaster|theatrical|directors?\s*cut|director['’]s\s*cut|final\s*cut|special\s*edition|ultimate\s*edition|anniversary|redux|restored)\b")
        .Select(m => Regex.Replace(m.Value.ToLowerInvariant(), @"\s+", " ")).Distinct().Order());
}
