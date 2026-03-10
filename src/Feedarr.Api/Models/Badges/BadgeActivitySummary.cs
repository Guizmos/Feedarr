namespace Feedarr.Api.Models.Badges;

/// <summary>Activity-log badge summary returned by ActivityRepository.</summary>
public sealed record BadgeActivitySummary(long LastActivityTs, int UnreadCount, string Tone);
