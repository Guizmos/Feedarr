namespace Feedarr.Api.Services;

public sealed record HistoryCleanupRequest(
    bool DryRun = true, int Limit = 100, int ScanLimit = 1000,
    string? AfterPoster = null, string? AfterStore = null, string? Confirmation = null);

public sealed class HistoryCleanupReport
{
    public bool DryRun { get; init; }
    public int GraceDays => 30;
    public long EntitiesBefore { get; set; }
    public long EntitiesAfter { get; set; }
    public long EntityCandidates { get; set; }
    public int EntityBatchCandidates { get; set; }
    public int EntitiesDeleted { get; set; }
    public int PostersScanned { get; set; }
    public int PosterCandidates { get; set; }
    public int PosterDeleteAttempts { get; set; }
    public string? WindowAfterPoster { get; init; }
    public string? WindowAfterStore { get; init; }
    public int PostersDeleted { get; set; }
    public long EstimatedRecoverableBytes { get; set; }
    public long ReclaimedBytes { get; set; }
    public long ProtectedEntities { get; set; }
    public int ProtectedPosters { get; set; }
    public Dictionary<string, long> ProtectionReasons { get; } = new();
    public long DeclaredStoreReferences { get; set; }
    public int StoresScanned { get; set; }
    public int MissingStoreDirectories { get; set; }
    public long MissingStoreReferenceRows { get; set; }
    public int MissingPosterFiles { get; set; }
    public int Errors { get; set; }
    public bool RestoreProtected { get; set; }
    public bool PublicationBusy { get; set; }
    public string? NextPoster { get; set; }
    public string? NextStore { get; set; }
    public bool MoreEntities => EntityCandidates > EntityBatchCandidates;
    public string ByteAccounting => "Logical file lengths only; actual disk recovery depends on compression, hard links and snapshots. No VACUUM is performed.";
    public string Scope => "Entity counts are global; file/store counts and byte estimates cover this bounded scan window only.";
    public string ProvenancePolicy => "Legacy entities with unknown provenance, manual data, Arr/request identities and all poster-match cache references are protected.";
}
