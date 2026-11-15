using System.Text.Json;
namespace Feedarr.Api.Services.Matching.Replay;

public sealed partial class MatchingReplayService
{
    private async Task<Evaluation> EvaluateAnimeAsync(MatchingShadowState expected, Session session, HttpClient? http, CancellationToken ct)
    {
        var raw = expected.RawTitle ?? "";
        var context = JikanCandidateAdapter.Context(raw);
        Evaluation MissingAnime(string reason) => new(Missing(expected, reason) with { Queries = context.Queries }, reason);
        if (http is null) return MissingAnime("offline_provider_responses_unavailable");
        if (SensitiveQuery(raw)) return MissingAnime("query_contains_sensitive_content");
        if (_retryAfterUtc > DateTimeOffset.UtcNow) return MissingAnime("provider_rate_limit_cooldown");
        if (!reader.JikanEnabled()) return MissingAnime("jikan_not_configured");
        var budget = session.CategoryBudgets.GetValueOrDefault("anime") ?? session.Budget;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var rows = await JikanCandidateAdapter.CollectAsync(raw, async (path, token) => {
                await Feedarr.Api.Services.Jikan.JikanClient.WaitForRequestAsync(token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.jikan.moe/v4/" + path);
                request.Options.Set(ReplayHttpBudget.Option, budget);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    _retryAfterUtc = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                return await TelevisionCandidates.ReadAsync(response, token).ConfigureAwait(false);
            }, deadline.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (reader.Read(expected.Id) != expected) return MissingAnime("persisted_state_changed_during_evaluation");
            var candidates = rows.Select(r => r.Candidate).ToArray();
            var ranked = SearchCandidateRanker.Rank(context, candidates);
            return new(new(expected.Id, "evaluated", Historical(expected), ranked with { PreserveExisting = expected.Protected }, Array.Empty<string>(), expected.Protected)
                { Queries = context.Queries, ProviderCandidates = candidates, InputState = expected },
                Fingerprint: ReplayEvidence.Provider(candidates), Complete: true, QueryPlan: ReplayEvidence.QueryPlan(context.Queries, "anime"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return MissingAnime("jikan_timeout"); }
        catch (ReplayBudgetExceededException) { return MissingAnime("jikan_http_budget"); }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("jikan_", StringComparison.Ordinal)) { return MissingAnime(ex.Message); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests) { return MissingAnime("provider_rate_limited"); }
        catch (Exception) { return MissingAnime("jikan_provider_unavailable"); }
    }
}
