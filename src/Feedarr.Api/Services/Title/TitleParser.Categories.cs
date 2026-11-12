using System.Text.RegularExpressions;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Titles;

public sealed partial class TitleParser
{
    // Per-call immutable input; policies are shared and contain no per-release state.
    private readonly record struct ParseInput(string Original, string Tokens, UnifiedCategory Category);

    private abstract class CategoryParser
    {
        private static readonly CategoryParser Video = new VideoCategoryParser();
        private static readonly CategoryParser Episodic = new EpisodicCategoryParser();
        private static readonly CategoryParser Broadcast = new BroadcastCategoryParser();
        private static readonly CategoryParser Game = new GameCategoryParser();

        public static CategoryParser For(UnifiedCategory category) => category switch
        {
            UnifiedCategory.JeuWindows => Game,
            UnifiedCategory.Serie or UnifiedCategory.Anime => Episodic,
            UnifiedCategory.Emission => Broadcast,
            // Film, Animation and Spectacle intentionally share video rules. Animation
            // does not inherit anime episode semantics solely because of its name.
            _ => Video
        };

        public abstract string Normalize(string raw, UnifiedCategory category);
        public virtual void ReadStructure(ParseInput input, ParsedTitle parsed) { }
        public abstract Match? FindYear(ParseInput input, TechnicalSuffix suffix);
        public abstract string CleanTitle(ParseInput input, ParsedTitle parsed, TechnicalSuffix suffix, Match? year);
    }

    private class VideoCategoryParser : CategoryParser
    {
        public override string Normalize(string raw, UnifiedCategory category)
        {
            var s = raw.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ');
            s = RxYearLangGlue.Replace(s, "${year} ${tag}");
            // Fansub labels describe the release, unlike arbitrary bracketed title words.
            s = Regex.Replace(s, @"^\[(?=[^\]]*(?i:subs|raws))[^\]]+\]\s*", "");
            if (category == UnifiedCategory.Anime &&
                (RxSeasonEpisode.IsMatch(s) || Regex.IsMatch(s, @"(?i)\b\d{3,4}[pi]\b")))
                s = Regex.Replace(s, @"(?i)^\[fansub\]\s*(?=\S)", "");
            return Regex.Replace(s, @"\s{2,}", " ").Trim();
        }

        public override Match? FindYear(ParseInput input, TechnicalSuffix suffix)
            => FindVideoReleaseYear(input.Tokens, input.Category, suffix);

        public override string CleanTitle(ParseInput input, ParsedTitle parsed, TechnicalSuffix suffix, Match? year)
            => CleanContextualTitle(input.Tokens, input.Category, parsed, suffix, year);
    }

    private sealed class EpisodicCategoryParser : VideoCategoryParser
    {
        public override void ReadStructure(ParseInput input, ParsedTitle parsed)
            => EpisodeRecognition.ParseSeriesTokens(input.Tokens, parsed);
    }

    private sealed class BroadcastCategoryParser : VideoCategoryParser
    {
        public override void ReadStructure(ParseInput input, ParsedTitle parsed)
            => EpisodeRecognition.ParseEmissionTokens(input.Tokens, parsed);
    }
}
