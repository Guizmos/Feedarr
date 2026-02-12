using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Feedarr.Api.Services.Matching;

public static class TitleNormalizer
{
    private static readonly Regex RxBracketTags = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex RxParensTags = new(@"\([^\)]*\)", RegexOptions.Compiled);
    private static readonly Regex RxSeasonEpisode = new(@"(?i)\bS\d{1,2}E\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex RxAltSeasonEpisode = new(@"\b\d{1,2}x\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex RxSeasonOnly = new(@"(?i)\bS\d{1,2}\b|\b(season|saison)\s*\d{1,2}\b", RegexOptions.Compiled);
    private static readonly Regex RxYear = new(@"\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex RxTags = new(@"(?i)\b(2160p|1080p|720p|480p|4k|8k|hdr10|hdr|dv|dovi|x264|x265|h\.?264|h\.?265|hevc|av1|xvid|divx|aac|dts|truehd|atmos|webrip|web[- .]?dl|bluray|bdrip|brrip|dvdrip|hdtv|remux|proper|repack|extended|uncut|limited|complete|collection|pack)\b", RegexOptions.Compiled);
    private static readonly Regex RxSpaces = new(@"\s{2,}", RegexOptions.Compiled);

    public static string NormalizeTitle(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var raw = input.Trim();
        var s = raw;
        s = RxBracketTags.Replace(s, " ");
        s = RxParensTags.Replace(s, " ");
        s = s.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ').Replace('+', ' ');

        s = RxSeasonEpisode.Replace(s, " ");
        s = RxAltSeasonEpisode.Replace(s, " ");
        s = RxSeasonOnly.Replace(s, " ");
        s = RxYear.Replace(s, " ");
        s = RxTags.Replace(s, " ");

        s = RemoveDiacritics(s.ToLowerInvariant());
        s = Regex.Replace(s, @"[^a-z0-9]+", " ");
        if (LooksLikePackedUppercase(raw))
        {
            var compact = Regex.Replace(s, @"\s+", "");
            if (compact.Length is >= 4 and <= 6)
                s = $"{compact[..^1]} {compact[^1]}";
        }
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();

        return s;
    }

    /// <summary>
    /// Normalisation stricte (moins agressive): minuscules + suppression accents,
    /// conserve les chiffres (ex: "1917") et ne retire pas les annees/tags.
    /// </summary>
    public static string NormalizeTitleStrict(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var normalized = RemoveDiacritics(input.Trim().ToLowerInvariant());
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append(' ');
            }
        }
        var result = RxSpaces.Replace(sb.ToString(), " ").Trim();
        return result;
    }

    /// <summary>
    /// Retourne des variantes utiles pour le matching (stricte + agressive).
    /// </summary>
    public static string[] BuildTitleVariants(string? input)
    {
        var strict = NormalizeTitleStrict(input);
        var loose = NormalizeTitle(input);
        if (string.IsNullOrEmpty(strict) && string.IsNullOrEmpty(loose)) return Array.Empty<string>();
        if (string.IsNullOrEmpty(loose) || loose == strict) return new[] { string.IsNullOrEmpty(strict) ? loose : strict };
        if (string.IsNullOrEmpty(strict)) return new[] { loose };
        return new[] { strict, loose };
    }

    /// <summary>
    /// Supprime les accents et diacritiques d'une chaîne.
    /// Ex: "Café" → "Cafe", "Müller" → "Muller"
    /// </summary>
    public static string RemoveDiacritics(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Normalisation légère pour le matching: minuscules + suppression accents.
    /// Garde la structure du titre (espaces, ponctuation basique).
    /// </summary>
    public static string NormalizeForMatching(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return RemoveDiacritics(input.Trim().ToLowerInvariant());
    }

    private static bool LooksLikePackedUppercase(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length < 4 || trimmed.Length > 6) return false;
        if (trimmed.Contains(' ') || trimmed.Contains('.') || trimmed.Contains('_') || trimmed.Contains('-')) return false;
        if (!trimmed.All(char.IsLetter)) return false;
        return trimmed.All(char.IsUpper);
    }
}
