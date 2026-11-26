using Feedarr.Api.Data.Repositories;

namespace Feedarr.Api.Services.Posters;

public static class PosterAudit
{
    public static void UpdateAttemptSuccess(
        ReleaseRepository releases,
        long releaseId,
        string? provider,
        string? providerId,
        string? lang,
        string? size,
        string? hash)
    {
        releases.UpdatePosterAttemptSuccess(releaseId, provider, providerId, lang, size, hash);
    }

    public static void UpdateAttemptFailure(
        ReleaseRepository releases,
        long releaseId,
        string? provider,
        string? providerId,
        string? lang,
        string? size,
        string? error)
    {
        releases.UpdatePosterAttemptFailure(releaseId, provider, providerId, lang, size, error);
        if (error?.StartsWith("v2:", StringComparison.Ordinal) == true && error.Split(':', 4) is { Length: 4 } parts)
            MatchingAttemptDiagnostics.Current?.SetOutcome(parts[1], parts[3].Split(';', 2)[0]);
    }
}
