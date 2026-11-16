using System.Reflection;
using System.Text.Json;

namespace Feedarr.Api.Services.Matching.Replay;

// Opt-in ranker input only: provider response bodies and request URLs never enter this DTO.
internal static class RankingCorpus
{
    internal const int MaximumBytes = 65536;
    internal static JsonElement? Create(MatchingShadowObservation observation)
    {
        if (observation.RankingContext is not { } context || observation.V2 is null ||
            observation.ProviderCandidates.Count > 100 || context.Queries.Count > 2) return null;
        var payload = JsonSerializer.SerializeToElement(new {
            Version = 1, ReleaseId = observation.ReleaseId,
            EngineVersion = typeof(RankingCorpus).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Pages = observation.CollectedPages, Context = context, Candidates = observation.ProviderCandidates,
            ExpectedDecision = observation.V2.Decision, ExpectedIdentity = observation.V2.SelectedIdentity
        });
        // Reject the whole input instead of redacting evidence and silently changing replay decisions.
        if (System.Text.Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaximumBytes || !Safe(payload)) return null;
        return payload;
    }
    private static bool Safe(JsonElement value) => value.ValueKind switch {
        JsonValueKind.String => value.GetString() is { Length: <= 512 } text && MatchingDiagnosticProjection.RedactionReason(text) is null,
        JsonValueKind.Object => value.EnumerateObject().All(p => Safe(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(Safe),
        _ => true
    };
    internal static CandidateRankingResult Replay(JsonElement payload)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaximumBytes || !Safe(payload) || payload.GetProperty("Version").GetInt32() != 1)
            throw new InvalidDataException("ranking_corpus_invalid");
        var context = payload.GetProperty("Context").Deserialize<CandidateRankingContext>()!;
        var candidates = payload.GetProperty("Candidates").Deserialize<ProviderSearchCandidate[]>()!;
        return SearchCandidateRanker.Rank(context, candidates);
    }
}
