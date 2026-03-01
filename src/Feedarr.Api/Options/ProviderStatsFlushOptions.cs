namespace Feedarr.Api.Options;

public sealed class ProviderStatsFlushOptions
{
    public bool EnableFlush { get; set; } = true;
    public int FlushIntervalSeconds { get; set; } = 5;
    public int MaxBatchSize { get; set; } = 500;
}
