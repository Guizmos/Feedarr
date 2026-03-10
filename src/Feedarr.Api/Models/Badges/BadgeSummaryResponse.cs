namespace Feedarr.Api.Models.Badges;

/// <summary>Response shape for GET /api/badges/summary.</summary>
public sealed record BadgeSummaryResponse(
    BadgeActivityPayload Activity,
    BadgeReleasesPayload Releases,
    BadgeSystemPayload System,
    BadgeSettingsPayload Settings);

/// <summary>Activity-log badge data.</summary>
public sealed record BadgeActivityPayload(
    int UnreadCount,
    long LastActivityTs,
    /// <summary>"info" | "warn" | "error"</summary>
    string Tone);

/// <summary>Releases badge data.</summary>
public sealed record BadgeReleasesPayload(
    int TotalCount,
    long LatestTs,
    /// <summary>Count of releases newer than releasesSinceTs. Null when releasesSinceTs == 0.</summary>
    int? NewSinceTsCount,
    /// <summary>
    /// Visual intent for the badge.
    /// "info"  — exact count available (newSinceTsCount is set).
    /// "warn"  — new releases detected via timestamp only, no exact count.
    /// </summary>
    string Tone);

/// <summary>System-state badge data.</summary>
public sealed record BadgeSystemPayload(
    bool IsSyncRunning,
    bool SchedulerBusy,
    int SourcesCount,
    /// <summary>
    /// System badge tone. Null when no actionable condition is detected.
    /// "warn"  — a blocking operation (backup/restore) is in progress.
    /// "error" — reserved for future hard-error conditions.
    /// </summary>
    string? Tone);

/// <summary>Settings badge data.</summary>
public sealed record BadgeSettingsPayload(
    int MissingExternalCount,
    bool HasAdvancedMaintenanceEnabled);
