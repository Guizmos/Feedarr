using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Tests;

/// <summary>
/// Unit tests for <see cref="BootstrapTokenService"/>.
/// Covers: single-use guarantee, expiry, IsValid vs TryConsume semantics,
/// InvalidateAll, and null/empty input safety.
/// </summary>
public sealed class BootstrapTokenServiceTests
{
    // -----------------------------------------------------------------------
    // IsValid
    // -----------------------------------------------------------------------

    [Fact]
    public void IsValid_FreshToken_ReturnsTrue()
    {
        var svc = new BootstrapTokenService();
        var token = svc.IssueToken();
        Assert.True(svc.IsValid(token));
    }

    [Fact]
    public void IsValid_UnknownToken_ReturnsFalse()
    {
        var svc = new BootstrapTokenService();
        Assert.False(svc.IsValid("deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValid_NullOrEmpty_ReturnsFalse(string? token)
    {
        var svc = new BootstrapTokenService();
        Assert.False(svc.IsValid(token));
    }

    // -----------------------------------------------------------------------
    // TryConsume — single-use guarantee
    // -----------------------------------------------------------------------

    [Fact]
    public void TryConsume_FirstCall_ReturnsTrue()
    {
        var svc = new BootstrapTokenService();
        var token = svc.IssueToken();
        Assert.True(svc.TryConsume(token));
    }

    [Fact]
    public void TryConsume_SecondCall_ReturnsFalse()
    {
        var svc = new BootstrapTokenService();
        var token = svc.IssueToken();

        Assert.True(svc.TryConsume(token));   // first use
        Assert.False(svc.TryConsume(token));  // token now used
    }

    [Fact]
    public void TryConsume_AfterConsume_IsValidReturnsFalse()
    {
        var svc = new BootstrapTokenService();
        var token = svc.IssueToken();

        svc.TryConsume(token);

        // IsValid should also report the token as invalid once consumed
        Assert.False(svc.IsValid(token));
    }

    [Fact]
    public void TryConsume_UnknownToken_ReturnsFalse()
    {
        var svc = new BootstrapTokenService();
        Assert.False(svc.TryConsume("deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryConsume_NullOrEmpty_ReturnsFalse(string? token)
    {
        var svc = new BootstrapTokenService();
        Assert.False(svc.TryConsume(token));
    }

    // -----------------------------------------------------------------------
    // InvalidateAll
    // -----------------------------------------------------------------------

    [Fact]
    public void InvalidateAll_MakesExistingTokensInvalid()
    {
        var svc = new BootstrapTokenService();
        var t1 = svc.IssueToken();
        var t2 = svc.IssueToken();

        svc.InvalidateAll();

        Assert.False(svc.IsValid(t1), "t1 should be invalid after InvalidateAll");
        Assert.False(svc.IsValid(t2), "t2 should be invalid after InvalidateAll");
        Assert.False(svc.TryConsume(t1), "TryConsume should fail after InvalidateAll");
    }

    [Fact]
    public void InvalidateAll_NewTokenAfterInvalidate_IsValid()
    {
        var svc = new BootstrapTokenService();
        svc.IssueToken();
        svc.InvalidateAll();

        var freshToken = svc.IssueToken();
        Assert.True(svc.IsValid(freshToken), "Freshly issued token should be valid after InvalidateAll");
    }

    // -----------------------------------------------------------------------
    // Multiple tokens can coexist independently
    // -----------------------------------------------------------------------

    [Fact]
    public void TwoDistinctTokens_IndependentConsumption()
    {
        var svc = new BootstrapTokenService();
        var t1 = svc.IssueToken();
        var t2 = svc.IssueToken();

        Assert.True(svc.TryConsume(t1));
        Assert.False(svc.TryConsume(t1));  // t1 already used
        Assert.True(svc.TryConsume(t2));   // t2 still valid
        Assert.False(svc.TryConsume(t2));  // t2 now used
    }

    // -----------------------------------------------------------------------
    // Thread-safety: concurrent consume — only one winner
    // -----------------------------------------------------------------------

    [Fact]
    public void TryConsume_ConcurrentCalls_OnlyOneSucceeds()
    {
        var svc = new BootstrapTokenService();
        var token = svc.IssueToken();

        const int threadCount = 20;
        var successCount = 0;

        var threads = Enumerable.Range(0, threadCount)
            .Select(_ => new Thread(() =>
            {
                if (svc.TryConsume(token))
                    Interlocked.Increment(ref successCount);
            }))
            .ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(1, successCount);
    }

    // -----------------------------------------------------------------------
    // ExpiresInSeconds is reasonable
    // -----------------------------------------------------------------------

    [Fact]
    public void ExpiresInSeconds_IsPositiveAndReasonable()
    {
        var svc = new BootstrapTokenService();
        Assert.InRange(svc.ExpiresInSeconds, 60, 3600);
    }
}
