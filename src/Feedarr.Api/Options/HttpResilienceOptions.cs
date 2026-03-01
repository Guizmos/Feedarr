namespace Feedarr.Api.Options;

public sealed class HttpResilienceOptions
{
    public ResilienceFamilyOptions Arr       { get; set; } = new();
    public ResilienceFamilyOptions Providers { get; set; } = new();
    public ResilienceFamilyOptions Indexers  { get; set; } = new();
}

public sealed class ResilienceFamilyOptions
{
    /// <summary>Consecutive failures before the circuit opens.</summary>
    public int MinimumThroughput    { get; set; } = 5;
    /// <summary>Seconds the circuit stays open before allowing a probe.</summary>
    public int BreakDurationSeconds { get; set; } = 30;
}
