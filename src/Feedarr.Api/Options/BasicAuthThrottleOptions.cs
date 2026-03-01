namespace Feedarr.Api.Options;

public sealed class BasicAuthThrottleOptions
{
    public int WindowSeconds { get; set; } = 300;
    public int SoftBlockThreshold { get; set; } = 5;
    public int MediumBlockThreshold { get; set; } = 10;
    public int HardBlockThreshold { get; set; } = 20;
    public int SoftBlockSeconds { get; set; } = 5;
    public int MediumBlockSeconds { get; set; } = 30;
    public int HardBlockSeconds { get; set; } = 300;
}
