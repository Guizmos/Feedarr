using System.Text.RegularExpressions;

namespace Feedarr.Api.Services.Matching;

public sealed record ShadowIdentity(string Provider, string Type, string Id);
public sealed record ShadowQuery(string Title, int? Year, string YearEvidence);
public sealed record ShadowOfficialTitle(string Title, string Kind, string Source, string? Language, string? MaskReason = null);
public sealed record ShadowCandidate(ShadowIdentity Identity, string Title, int? Year, double Confidence,
    string Decision, IReadOnlyList<string> Reasons, IReadOnlyList<string> Contradictions, IReadOnlyList<ShadowOfficialTitle>? OfficialTitles = null,
    IReadOnlyList<ShadowOfficialTitle>? CompoundTitles = null, int? TrailingYearHypothesis = null);
public sealed record ShadowEntry(long ReleaseId, string RawTitle, string HistoricalDecision, ShadowIdentity? HistoricalIdentity,
    double? HistoricalScore, string Decision, ShadowIdentity? SelectedIdentity, bool Divergence,
    double DurationMs, bool PreserveExisting, IReadOnlyList<string> Reasons,
    IReadOnlyList<ShadowQuery> Queries, IReadOnlyList<ShadowCandidate> Candidates, string? RawTitleMaskReason = null);
internal static class MatchingDiagnosticProjection
{
    internal static ShadowEntry Project(MatchingShadowObservation observation, string? rawTitle,
        double durationMs)
    {
        var result = observation.V2;
        var decision = result?.Decision.ToString().ToLowerInvariant() ?? "not_evaluable";
        var historical = observation.Historical;
        // A different identity is comparable only when the historical side actually accepted/rejected.
        var comparable = historical.Decision is "Accept" or "Reject" && result is not null;
        var divergence = comparable && (decision != historical.Decision.ToLowerInvariant() ||
            (result!.Decision == CandidateDecision.Accept && result.SelectedIdentity != historical.Identity));
        var candidates = result?.Candidates.Select(c =>
        {
            return new ShadowCandidate(Identity(c.Identity)!, SafeText(observation.ProviderCandidates.FirstOrDefault(p => p.Identity == c.Identity)?.Title), observation.ProviderCandidates.FirstOrDefault(p => p.Identity == c.Identity)?.Year, c.Confidence,
                c.Decision.ToString().ToLowerInvariant(), c.PositiveReasons.ToArray(), c.Contradictions.Select(x => x.Code).ToArray(),
                observation.ProviderCandidates.Where(p => p.Identity == c.Identity).SelectMany(p => p.OfficialTitles ?? Array.Empty<OfficialProviderTitle>())
                    .Distinct().Select(ProjectTitle).ToArray(),
                c.CompoundTitle is { } proof ? new[] { ProjectTitle(proof.First), ProjectTitle(proof.Second) } : null,
                c.TrailingYearHypothesis);
        }).ToArray() ?? Array.Empty<ShadowCandidate>();
        var queries = observation.Queries.Select(q => new ShadowQuery(SafeText(q.Title), q.Year, q.YearEvidence.ToString())).ToArray();
        return new ShadowEntry(observation.ReleaseId, SafeText(rawTitle), historical.Decision,
            Identity(historical.Identity), historical.Confidence, decision, Identity(result?.SelectedIdentity), divergence,
            double.IsFinite(durationMs) ? Math.Max(0, durationMs) : 0, observation.PreserveExisting,
            observation.MissingInformation.Concat(result?.Reasons ?? Array.Empty<string>()).ToArray(), queries, candidates, RedactionReason(rawTitle));
    }

    private static ShadowIdentity? Identity(SearchIdentity? identity) => identity is null ? null :
        new(identity.Provider, identity.MediaType == SearchMediaType.Movie ? "movie" : identity.MediaType == SearchMediaType.Series ? "tv" : "unknown", identity.Id);

    private static ShadowOfficialTitle ProjectTitle(OfficialProviderTitle title)
    {
        var language = title.Language is null ? null : SafeText(title.Language);
        return new(SafeText(title.Title), title.Kind.ToString(), title.Source.ToString(),
            language is null ? null : language[..Math.Min(16, language.Length)], RedactionReason(title.Title));
    }

    // Same redaction policy as V2.3-H: explain false positives without exposing matched text or relaxing the guard.
    private static readonly (string Reason, Regex Pattern)[] Redactions =
    {
        ("url", new(@"https?://|www\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20))),
        ("path_or_separator", new(@"[/\\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20))),
        ("email_or_at_sign", new(@"@", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20))),
        ("credential_keyword", new(@"token|passkey|api[_-]?key|bearer|password|secret", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20))),
        ("domain_like_text", new(@"\b[\w-]+\.(?:com|net|org|fr|io|tv|me|xyz)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
    };
    internal static string? RedactionReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var rule in Redactions) if (rule.Pattern.IsMatch(value)) return rule.Reason;
        return null;
    }
    internal static string SafeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (RedactionReason(value) is not null) return "[titre masqué : contenu sensible possible]";
        return new string(value.Where(c => !char.IsControl(c)).Take(512).ToArray());
    }
}
