using System.Text.RegularExpressions;
using Feedarr.Api.Models;

namespace Feedarr.Api.Services.Titles;

public sealed partial class TitleParser
{
    private sealed record TechnicalSuffix(int Start, bool Rich, string? Resolution, string? Source, string? Codec, string? Group);

    // Shared technical recognition; category policies consume this result without re-tokenizing.
    private static class TechnicalRecognition
    {
        private sealed record SuffixToken(int Start, int End, string Value, string Kind, string? Normalized = null)
        {
            public bool Anchor => Kind is "source" or "codec" or "resolution" or "video" or "version" or "build" or "platform";
        }

        private sealed class TokenVocabulary
        {
            public Regex Pattern { get; }
            public (int Group, string Kind, string? Value)[] Rules { get; }

            public TokenVocabulary(Regex pattern, bool game)
            {
                Pattern = pattern;
                var rules = new List<(int, string, string?)>();
                if (!game)
                {
                    for (var i = 0; i < Sources.Length; i++) rules.Add((pattern.GroupNumberFromName($"source{i}"), "source", Sources[i].value));
                    for (var i = 0; i < Codecs.Length; i++) rules.Add((pattern.GroupNumberFromName($"codec{i}"), "codec", Codecs[i].value));
                }
                foreach (var kind in game ? new[] { "version", "build", "naked", "platform", "language", "scene", "edition", "distribution", "metadata" }
                             : new[] { "resolution", "video", "flag", "service", "language" })
                    rules.Add((pattern.GroupNumberFromName(kind), kind, null));
                Rules = rules.ToArray();
            }
        }

        // One ordered vocabulary per medium. Long compounds precede short tokens. Lazy
        // construction avoids static initialization ordering across partial class files.
        private static readonly Lazy<TokenVocabulary> VideoTokenLexer = new(() => new TokenVocabulary(new Regex(string.Join("|",
            Sources.Select((x, i) => $"(?<source{i}>{x.rx})")
                .Concat(Codecs.Select((x, i) => $"(?<codec{i}>{x.rx})"))
                .Concat(new[]
                {
                    $"(?<resolution>{RxRes})",
                    @"(?<video>(?i)\b(?:\d{3,4}[pi]|WEB|XVID|DIVX|DTS(?:[ .-]?(?:HDHRA|HD|X|MA))*|E[ .-]?AC[ .-]?3|AC-3|EAC3|AAC(?:[ .]?\d[ .]\d)?|DDP?\d?|TRUEHD|ATMOS|FLAC|OPUS|MP3|PROPER)\b)",
                    @"(?<flag>(?i)\b(?:SD|SDR|AD|PCM|BD|LC|NVENC|FULL|UHD|HYBRID|CUSTOM|EXTENDED|REMASTERED|RESTORED|HDR10PLUS|HDR10P|HDR(?:10\+?)?|DV|DOVI|P7|DOLBY[ .]+VISION|4KLIGHT|HDLIGHT(?:DD\d)?|\d{1,2}[ .]*bits?|[2571][ .][01]|\dch|3D|SBS|HSBS|REMUX|PAL|NTSC|SECAM|MKV|MP4|AVI|MULTIPLE[ .]+SUBTITLES?)\b)",
                    @"(?<service>(?i)\b(?:AMZN|DSNP|M6P|TF1P|ADN|CR|ATV|NF|CNLP|PAPR|IT|TVING|FTV|HMAX|MUBI|DISNEY)\b)",
                    @"(?<language>(?i)\b(?:MULTI\d*|DUAL|VFF|VFQ|VFI|VF\d*|VOSTFR|VOST|VOF|TRUEFRENCH|SUBFRENCH|FRENCH|ENGLISH|ENG|FR|EN|VO)\b)"
                })), RegexOptions.Compiled), false));

        private static readonly Lazy<TokenVocabulary> GameTokenLexer = new(() => new TokenVocabulary(new Regex(string.Join("|", new[]
        {
            $"(?<version>{RxGameVersion})", $"(?<build>{RxGameBuild}|{RxGameSlashBuild})",
            $"(?<naked>{RxGameVersionNaked})", $"(?<platform>{RxGamePlatform})",
            $"(?<language>{RxGameLang})", $"(?<scene>{RxGameScene})",
            $"(?<edition>{RxGameEditionShort})",
            "(?<metadata>" + string.Join("|", GameMetadataPatterns.Where(rx => rx != RxGameVersion && rx != RxGameBuild && rx != RxGameSlashBuild && rx != RxGameEditionShort).Select(rx => rx.ToString())) + ")",
            @"(?<distribution>(?i)(?:^|[._+-])(?:complete|conversion)(?=[._+-]|$))"
        }), RegexOptions.Compiled), true));

        private static List<SuffixToken> ReadSuffixTokens(string raw, bool game, bool forGroup = false)
        {
            var result = new List<SuffixToken>();
            var scan = game ? new string(raw.Select(c => char.IsWhiteSpace(c) || "[]():".Contains(c) ? '.' : c).ToArray()) : raw;
            var vocabulary = game ? GameTokenLexer.Value : VideoTokenLexer.Value;
            foreach (Match match in vocabulary.Pattern.Matches(scan))
            {
                foreach (var rule in vocabulary.Rules)
                {
                    if (!match.Groups[rule.Group].Success) continue;
                    if (rule.Kind == "distribution" && !forGroup) break;
                    result.Add(new(match.Index, match.Index + match.Length, match.Value, rule.Kind, rule.Value));
                    break;
                }
            }
            return result;
        }

        private static bool TokenGap(string value)
            => value.All(c => char.IsWhiteSpace(c) || char.IsDigit(c) || "._+-/()[]".Contains(c));

        private static bool TerminalSignature(string value)
            => string.IsNullOrWhiteSpace(value.Trim(' ', '.', '(', ')', '[', ']')) ||
               Regex.IsMatch(value, @"^\s*[-_]\s*[A-Za-z0-9][A-Za-z0-9 ._\-]{0,50}[\])]?[\s.]*$") ||
               Regex.IsMatch(value, @"(?i)^\s*[\])]?(?:\s*\[[a-f0-9]{8}\])?\s*$");

        private static bool EstablishedSequence(string raw, List<SuffixToken> tokens, int index)
        {
            var anchors = tokens[index].Anchor ? 1 : 0;
            var end = tokens[index].End;
            for (var j = index + 1; j < tokens.Count; j++)
            {
                if (!TokenGap(raw[end..tokens[j].Start])) break;
                if (tokens[j].Anchor && ++anchors >= 2) return true;
                end = tokens[j].End;
            }
            // Once two independent technical tokens agree, later unknown text is suffix
            // content, not a reason to put all the preceding technical tokens in the title.
            return anchors >= 2 || TerminalSignature(raw[end..]);
        }

        private static int FindVideoTechnicalStart(string raw, List<SuffixToken> tokens)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Start == 0 || !tokens[i].Anchor || !EstablishedSequence(raw, tokens, i)) continue;
                var start = tokens[i].Start;
                var rich = tokens.Skip(i).Any(t => t.Kind is "source" or "codec" ||
                    (t.Kind == "video" && !Regex.IsMatch(t.Value, @"^\d{3,4}[pi]$", RegexOptions.IgnoreCase)));
                for (var j = i - 1; j >= 0 && TokenGap(raw[tokens[j].End..start]) && !raw[tokens[j].End..start].Any(char.IsDigit); j--)
                {
                    var token = tokens[j];
                    // Leave languages to TrimVideoSuffix: it tracks their removal as
                    // evidence for the existing edition/pack cleanup rules.
                    if (token.Kind == "language") break;
                    if (token.Kind is "flag" or "service")
                    {
                        var yearBefore = Regex.IsMatch(raw[..token.Start].TrimEnd(' ', '.', '_'), @"\b(?:19|20)\d{2}$");
                        if (!rich && !yearBefore) break;
                        if (token.Kind == "service" || Regex.IsMatch(token.Value, @"(?i)^(SD|AD|PCM)$"))
                        {
                            var previousMetadata = j > 0 && TokenGap(raw[tokens[j - 1].End..token.Start]) &&
                                (tokens[j - 1].Kind is "language" or "flag");
                            if (!yearBefore && (!previousMetadata || token.Value != token.Value.ToUpperInvariant())) break;
                        }
                    }
                    start = token.Start;
                }
                // Keep a recognized technical block atomic, including its opening delimiter.
                var open = Math.Max(raw.LastIndexOf('[', start), raw.LastIndexOf('(', start));
                var close = Math.Max(raw.LastIndexOf(']', start), raw.LastIndexOf(')', start));
                if (open > close && TokenGap(raw[(open + 1)..start])) start = open;
                return start > 0 ? start : tokens[i].Start;
            }
            return raw.Length;
        }

        private static int FindGameMetadataStart(string raw, List<SuffixToken> tokens)
        {
            var boundary = raw.Length;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == "distribution") continue; // Evidence within a suffix, never a work boundary by itself.
                // An edition is not disposable merely because language/platform tags follow.
                // Retain legacy cleanup only inside an explicit version/build distribution.
                if (token.Kind == "edition" && !tokens.Skip(i + 1).Any(t => t.Kind is "version" or "build"))
                {
                    var block = Math.Max(raw.LastIndexOf('(', token.Start), raw.LastIndexOf('[', token.Start));
                    var inMetadataBlock = block >= 0 && (block == token.Start || TokenGap(raw[(block + 1)..token.Start])) &&
                        raw.IndexOf(raw[block] == '[' ? ']' : ')', token.End) >= 0 &&
                        tokens.Skip(i + 1).Any(t => t.Kind == "metadata");
                    if (!inMetadataBlock) continue;
                }
                var start = token.Start;
                if (start == 0 || (!"._+-".Contains(raw[start]) && char.IsLetterOrDigit(raw[start - 1]))) continue;
                if (token.Kind == "platform" && !Regex.IsMatch(token.Value.Trim(' ', '.', '_', '-'),
                        @"(?i)^(PC|winmac|macwin|linux|macos|windows|x64|x86|win64|win32|amd64|gog|steam|egs|iso|win|mac)$")) continue;
                var accepted = token.Kind is "version" or "build" or "platform"
                    ? EstablishedSequence(raw, tokens, i)
                    : GameTailIsKnown(raw, tokens, i);
                if (token.Kind is "version" or "build")
                {
                    var block = Math.Max(raw.LastIndexOf('(', start), raw.LastIndexOf('[', start));
                    if (block >= 0 && (block == start || TokenGap(raw[(block + 1)..start])) &&
                        raw.IndexOf(raw[block] == '[' ? ']' : ')', token.End) >= 0)
                    {
                        accepted = true; // Explicit version/build in a closed metadata block.
                        start = block;
                    }
                }
                if (token.Kind == "naked")
                {
                    while (start < raw.Length && "._+-".Contains(raw[start])) start++;
                    var parts = raw[start..token.End].Split('.');
                    accepted = !(parts.Length == 2 && parts.Any(part => part.Length == 4)) &&
                        (parts.Length >= 3 || tokens.Skip(i + 1).Any(t => t.Kind is "platform" or "scene")) &&
                        GameTailIsKnown(raw, tokens, i);
                    if (accepted && parts[0].Length == 4) start += parts[0].Length + 1;
                }
                if (token.Kind == "language")
                    accepted &= (tokens.Any(t => t.Anchor || t.Kind is "scene" or "naked")) &&
                        (token.Value.Trim(' ', '.', '_', '-') == token.Value.Trim(' ', '.', '_', '-').ToUpperInvariant() ||
                        RxContextHardLanguage.IsMatch(token.Value.Trim(' ', '.', '_', '-')));
                if (token.Kind == "scene") accepted = raw[token.Start] == '-' ||
                    (raw[start] == '[' && raw.IndexOf(']', token.End) >= 0 && tokens.Skip(i + 1).Any(t => t.Kind == "metadata")) ||
                    (accepted && (raw[token.Start] == '[' || boundary < raw.Length || tokens.Skip(i + 1).Any(t => t.Kind == "metadata")));
                if (!accepted) continue;
                var open = Math.Max(raw.LastIndexOf('(', start), raw.LastIndexOf('[', start));
                var close = Math.Max(raw.LastIndexOf(')', start), raw.LastIndexOf(']', start));
                var inBlock = open >= 0 && open > close;
                if (inBlock && token.Kind is "metadata" or "platform" or "language")
                {
                    // Do not leave half a bracketed producer tag or DLC count in TitleClean.
                    if (start > open && !TokenGap(raw[(open + 1)..start])) continue;
                    start = open;
                }
                if (token.Kind == "metadata")
                {
                    var number = Regex.Match(token.Value, @"^[._-]?\d+(?=[._-](?i:bonus|ost|dlc))");
                    if (number.Success && !inBlock) start += number.Length;
                }
                boundary = Math.Min(boundary, start);
            }
            return boundary;
        }

        private static bool GameTailIsKnown(string raw, List<SuffixToken> tokens, int first)
        {
            var signature = RxGameReleaseGroup.Match(raw);
            var end = signature.Success ? signature.Index : raw.Length;
            var cursor = tokens[first].Start;
            for (var i = first; i < tokens.Count && tokens[i].End <= end; i++)
            {
                if (!TokenGap(raw[cursor..tokens[i].Start])) return false;
                cursor = tokens[i].End;
            }
            return cursor <= end && TokenGap(raw[cursor..end]);
        }

        public static TechnicalSuffix Analyze(string raw, string original, UnifiedCategory category)
        {
            var game = category == UnifiedCategory.JeuWindows;
            var tokens = ReadSuffixTokens(raw, game);
            var boundary = game ? FindGameMetadataStart(raw, tokens) : FindVideoTechnicalStart(raw, tokens);
            var suffix = tokens.Where(t => t.Start >= boundary).ToList();
            var source = suffix.FirstOrDefault(t => t.Kind == "source")?.Normalized;
            // Retain the existing codec priority, including the MPEG2/AC3 combination.
            var codecs = suffix.Where(t => t.Kind == "codec").Select(t => t.Normalized).ToHashSet();
            var codec = codecs.Contains("MPEG2") && codecs.Contains("AC3") ? "MPEG2/AC3" :
                Codecs.Select(c => c.value).FirstOrDefault(codecs.Contains);
            var resolution = suffix.FirstOrDefault(t => t.Kind == "resolution");
            var gameResolution = game ? RxRes.Match(raw) : Match.Empty;
            return new(boundary, suffix.Any(t => t.Kind is "source" or "codec" ||
                    (t.Kind == "video" && !Regex.IsMatch(t.Value, @"^\d{3,4}[pi]$", RegexOptions.IgnoreCase))),
                gameResolution.Success ? NormalizeResolution(gameResolution.Value) : resolution is null ? null : NormalizeResolution(resolution.Value), source, codec,
                ExtractSequenceGroup(original, category, boundary < raw.Length));
        }

        private static string? ExtractSequenceGroup(string raw, UnifiedCategory category, bool hasSuffix)
        {
            // Search after technical context, rather than blindly taking the last hyphen
            // of a compound group or a natural title such as Half-Life.
            var originalTokens = ReadSuffixTokens(raw, category == UnifiedCategory.JeuWindows, forGroup: true);
            foreach (Match dash in Regex.Matches(raw, "[-_]"))
            {
                if (originalTokens.Any(t => t.Start < dash.Index && t.End > dash.Index)) continue;
                var group = raw[(dash.Index + 1)..].Trim();
                if (group.Length is < 2 or > 30 || !Regex.IsMatch(group, @"^[A-Za-z0-9_]+(?:[.\-][A-Za-z0-9_]+){0,4}$")) continue;
                var previous = originalTokens.LastOrDefault(t => t.End <= dash.Index);
                var gap = previous is null ? "" : raw[previous.End..dash.Index];
                var context = hasSuffix;
                if (previous is not null)
                {
                    for (var i = originalTokens.IndexOf(previous); i >= 0; i--)
                    {
                        if (originalTokens[i].Anchor) { context = true; break; }
                        if (i == 0 || !TokenGap(raw[originalTokens[i - 1].End..originalTokens[i].Start])) break;
                    }
                }
                // Unknown words do not establish a separator inside free text. A spaced
                // dash after suffix evidence is an explicit terminal signature delimiter.
                var compact = gap.TrimStart(' ', '.', '_', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd(' ', '.', '_');
                var technicalGap = compact.Length > 0 && ReadSuffixTokens(compact, category == UnifiedCategory.JeuWindows)
                    .Any(t => t.Start == 0 && t.End == compact.Length && t.Anchor);
                var adjacent = previous is not null && previous.Start > 0 && context &&
                    (TokenGap(gap) || technicalGap ||
                        (dash.Index > 0 && char.IsWhiteSpace(raw[dash.Index - 1]) &&
                         dash.Index + 1 < raw.Length && char.IsWhiteSpace(raw[dash.Index + 1]) &&
                         Regex.IsMatch(gap, @"^[._ ]+[A-Za-z]+[._ ]*$")));
                if (dash.Value == "_" && (char.IsDigit(group[0]) || previous?.End != dash.Index)) continue;
                // A separator must follow suffix evidence, not an arbitrary earlier token
                // in the work name or an alternate title. Known terminal scene names also
                // support releases whose distribution label is not in the vocabulary.
                var scene = category == UnifiedCategory.JeuWindows && RxGameScene.IsMatch("-" + group);
                if ((!adjacent || !(previous!.Anchor || previous.Kind is "metadata" or "language" or "flag" or "scene" or "edition" or "distribution")) && !scene)
                    continue;
                if (category == UnifiedCategory.JeuWindows && ReadSuffixTokens(group, true)
                    .Any(t => t.Kind is "version" or "build" || (t.Kind == "platform" && t.End < group.Length))) continue;
                var withoutExtension = Regex.Replace(group, @"(?i)\.(?:mkv|mp4|avi)$", "");
                if (category != UnifiedCategory.JeuWindows && ReadSuffixTokens(withoutExtension, false).Any(t => t.Anchor)) continue;
                if (hasSuffix || category == UnifiedCategory.JeuWindows) return group;
            }
            return null;
        }
    }
}
