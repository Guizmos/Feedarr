using System.Text.RegularExpressions;

namespace Feedarr.Api.Services.Matching;

// Search text and distribution scope are separate. Removing a query suffix must
// never turn a bundle, named expansion or commercial edition into a base-game claim.
internal sealed record GameSearchScope(string? BareEdition, string? Content, string PlatformOrigin)
{
    internal static GameSearchScope Read(string raw)
    {
        var words = Regex.Replace(raw.Replace('.', ' ').Replace('_', ' ').Replace('-', ' '), @"\s+", " ");
        var bare = Regex.Match(words, @"(?i)\b(?<edition>deluxe|ultimate|definitive|gold)[)\]]?\s+(?=[([]?\s*(?:v\d|build\s*\d))");
        var content = Regex.IsMatch(words, @"(?i)\bgame\s*&\s*soundtrack\b") ? "game_with_soundtrack" :
            Regex.IsMatch(words, @"(?i)\b(?:ultimate|complete|deluxe)\s+bundle\b") ? "commercial_bundle" :
            Regex.IsMatch(words, @"(?i)\b(?:all|\d+|incl)\s*dlcs?\b") ? "included_dlcs" : null;
        var origin = Regex.IsMatch(words, @"(?i)\b(?:recompiled|recompilation)\b") ? "recompiled" :
            Regex.IsMatch(words, @"(?i)\b(?:portage|port)\s+(?:ps[1-5]|switch|console)\b") ? "console_port" : "unspecified";
        return new(bare.Success ? bare.Groups["edition"].Value.ToLowerInvariant() : null, content, origin);
    }

    internal string Query(string title)
    {
        if (BareEdition is not null)
            title = Regex.Replace(title, @"(?i)\s+[([]?\s*" + Regex.Escape(BareEdition) + @"[)\]]?$", "");
        // The parser already recognized Soundtrack as technical metadata but left
        // its introductory conjunction in the title. Require the original phrase.
        if (Content == "game_with_soundtrack")
            title = Regex.Replace(title, @"(?i)\s+game\s*&(?:\s+soundtrack)?$", "");
        return title.Trim();
    }
}
