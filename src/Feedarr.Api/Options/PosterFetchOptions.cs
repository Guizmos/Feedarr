namespace Feedarr.Api.Options;

public sealed class PosterFetchOptions
{
    public int MaxAttempts { get; set; } = 3;
    public int RequestTimeoutSeconds { get; set; } = 60;
    public int RefreshTtlDays { get; set; } = 30;
    public int MinIntervalMs { get; set; } = 250;
    public int MaxItemDurationSeconds { get; set; } = 60;
    public int[] RetryDelaysSeconds { get; set; } = [2, 5, 15];
}
