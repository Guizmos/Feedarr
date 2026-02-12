using Feedarr.Api.Models;
using Feedarr.Api.Services.TvMaze;

namespace Feedarr.Api.Services.Matching;

public static class TvMazeScorer
{
    public static float ScoreCandidate(
        string queryTitle,
        int? queryYear,
        UnifiedCategory queryCategory,
        TvMazeClient.ShowResult candidate,
        PosterMatchIds? knownIds)
    {
        var score = MatchScorer.ScoreCandidate(
            queryTitle,
            queryYear,
            queryCategory,
            candidate.Name,
            null,
            candidate.PremieredYear,
            "series");

        if (knownIds is not null)
        {
            var idBonus = 0f;
            if (candidate.TvdbId.HasValue && knownIds.TvdbId.HasValue && candidate.TvdbId == knownIds.TvdbId)
                idBonus = 0.15f;
            if (!string.IsNullOrWhiteSpace(candidate.ImdbId) && !string.IsNullOrWhiteSpace(knownIds.ImdbId) &&
                string.Equals(candidate.ImdbId, knownIds.ImdbId, StringComparison.OrdinalIgnoreCase))
                idBonus = Math.Max(idBonus, 0.15f);

            score += idBonus;
        }

        if (score < 0f) return 0f;
        if (score > 1f) return 1f;
        return score;
    }
}
