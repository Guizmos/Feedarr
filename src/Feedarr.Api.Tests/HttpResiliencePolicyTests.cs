using System.Net;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

/// <summary>
/// Tests for the circuit breaker + transient retry handler chain.
/// </summary>
public sealed class HttpResiliencePolicyTests
{
    // ─── (a) ────────────────────────────────────────────────────────────────────
    // 2×500 then 200 → inner called 3 times; final success resets the CB.

    [Fact]
    public async Task CircuitBreakerAndRetry_TwoTransientsThenSuccess_ThreeCallsTotal()
    {
        var state = new ArrCircuitBreakerState();
        var responses = new Queue<HttpStatusCode>([
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.OK
        ]);
        var callCount = 0;

        using var inner   = new LambdaHandler(_ => { callCount++; return new HttpResponseMessage(responses.Dequeue()); });
        using var retry   = new TransientHttpRetryHandler { InnerHandler = inner };
        using var cb      = MakeCb(state, threshold: 5);
        cb.InnerHandler   = retry;
        using var invoker = new HttpMessageInvoker(cb);

        using var req  = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        using var resp = await invoker.SendAsync(req, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, callCount);
        Assert.False(state.IsOpen);
    }

    // ─── (b) ────────────────────────────────────────────────────────────────────
    // 401 → inner called exactly once; no retry (4xx is not transient);
    // CB stays closed (4xx is neutral — not counted as server failure).

    [Fact]
    public async Task Retry_ClientError401_NoRetry_NoBreakerOpen()
    {
        var state     = new ArrCircuitBreakerState();
        var callCount = 0;

        using var inner   = new LambdaHandler(_ => { callCount++; return new HttpResponseMessage(HttpStatusCode.Unauthorized); });
        using var retry   = new TransientHttpRetryHandler { InnerHandler = inner };
        using var cb      = MakeCb(state, threshold: 5);
        cb.InnerHandler   = retry;
        using var invoker = new HttpMessageInvoker(cb);

        using var req  = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        using var resp = await invoker.SendAsync(req, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(1, callCount);
        Assert.False(state.IsOpen);
    }

    // ─── (c) ────────────────────────────────────────────────────────────────────
    // An internal timeout (TaskCanceledException with an internal, already-cancelled token)
    // must propagate as OperationCanceledException AND be counted as a CB failure.

    [Fact]
    public async Task CircuitBreaker_InternalTimeout_PropagatesAndCountsAsFailure()
    {
        var state = new ArrCircuitBreakerState();

        using var inner   = new TimeoutSimHandler();
        using var cb      = MakeCb(state, threshold: 5);
        cb.InnerHandler   = inner;
        using var invoker = new HttpMessageInvoker(cb);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoker.SendAsync(req, CancellationToken.None));

        Assert.Equal(1, state.ConsecutiveFailuresForTest);
    }

    // ─── (d) ────────────────────────────────────────────────────────────────────
    // After N consecutive 5xx the circuit opens; the next call fast-fails with
    // HttpRequestException WITHOUT calling the inner handler.

    [Fact]
    public async Task CircuitBreaker_AfterThreshold_OpensFastFailsWithoutCallingInner()
    {
        const int threshold = 3;
        var state     = new ArrCircuitBreakerState();
        var callCount = 0;

        using var inner   = new LambdaHandler(_ => { callCount++; return new HttpResponseMessage(HttpStatusCode.InternalServerError); });
        using var cb      = MakeCb(state, threshold: threshold, breakSeconds: 60);
        cb.InnerHandler   = inner;
        using var invoker = new HttpMessageInvoker(cb);

        // Exhaust the failure threshold
        for (var i = 0; i < threshold; i++)
        {
            using var req  = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
            using var resp = await invoker.SendAsync(req, CancellationToken.None);
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        }

        Assert.True(state.IsOpen);
        Assert.Equal(threshold, callCount);

        // Next request must fast-fail without reaching inner
        using var failReq = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            invoker.SendAsync(failReq, CancellationToken.None));

        Assert.Equal(threshold, callCount); // inner NOT called on fast-fail
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static ArrCircuitBreakerHandler MakeCb(
        ArrCircuitBreakerState state, int threshold = 5, int breakSeconds = 30)
    {
        var opts = OptionsFactory.Create(new HttpResilienceOptions
        {
            Arr = new ResilienceFamilyOptions
            {
                MinimumThroughput    = threshold,
                BreakDurationSeconds = breakSeconds,
            }
        });
        return new ArrCircuitBreakerHandler(
            state, opts, NullLogger<ArrCircuitBreakerHandler>.Instance);
    }

    /// <summary>Calls a user-provided factory for each request.</summary>
    private sealed class LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(fn(request));
    }

    /// <summary>
    /// Simulates an internal HTTP client timeout: throws TaskCanceledException
    /// using its own internal CancellationTokenSource, so the outer CT is NOT
    /// cancelled (mimicking a per-request timeout rather than user cancellation).
    /// </summary>
    private sealed class TimeoutSimHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            throw new TaskCanceledException("Simulated internal timeout", null, cts.Token);
        }
    }
}
