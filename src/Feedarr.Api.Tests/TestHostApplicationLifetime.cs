using Microsoft.Extensions.Hosting;

namespace Feedarr.Api.Tests;

internal sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => _stopped.Token;

    public void StopApplication()
    {
        if (!_stopping.IsCancellationRequested)
            _stopping.Cancel();
    }

    public void Dispose()
    {
        _started.Dispose();
        _stopping.Dispose();
        _stopped.Dispose();
    }
}
