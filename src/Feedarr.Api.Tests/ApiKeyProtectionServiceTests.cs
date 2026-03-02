using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Feedarr.Api.Tests;

/// <summary>
/// Unit tests for ApiKeyProtectionService.
///
/// Key invariant: a plaintext value (no "ENC:" prefix) must be returned as-is
/// from Unprotect() without throwing, regardless of whether the DataProtection
/// key ring is available. This ensures that DBs migrated from pre-encryption
/// versions continue to work.
/// </summary>
public sealed class ApiKeyProtectionServiceTests
{
    // ------------------------------------------------------------------ //
    //  A) Unprotect(plaintext) → returns plaintext, no exception          //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Unprotect_Plaintext_ReturnsPlaintextWithoutThrowing()
    {
        var svc = CreateService();

        var result = svc.Unprotect("my-api-key-plain");

        Assert.Equal("my-api-key-plain", result);
    }

    // ------------------------------------------------------------------ //
    //  B) Unprotect("") → returns "", no exception                        //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Unprotect_EmptyString_ReturnsEmptyString()
    {
        var svc = CreateService();

        var result = svc.Unprotect("");

        Assert.Equal("", result);
    }

    // ------------------------------------------------------------------ //
    //  C) Protect(plaintext) → IsProtected returns true on result         //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Protect_ThenIsProtected_ReturnsTrue()
    {
        var svc = CreateService();

        var encrypted = svc.Protect("my-key");

        Assert.True(svc.IsProtected(encrypted));
        Assert.StartsWith("ENC:", encrypted);
    }

    // ------------------------------------------------------------------ //
    //  D) Protect → Unprotect roundtrip returns original value            //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Protect_ThenUnprotect_RoundtripsValue()
    {
        var svc = CreateService();

        var encrypted = svc.Protect("round-trip-key");
        var decrypted = svc.Unprotect(encrypted);

        Assert.Equal("round-trip-key", decrypted);
    }

    // ------------------------------------------------------------------ //
    //  E) Protect is idempotent: already-protected value is unchanged     //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Protect_AlreadyProtectedValue_IsNotDoubleEncrypted()
    {
        var svc = CreateService();

        var encrypted = svc.Protect("idempotent-key");
        var reProtected = svc.Protect(encrypted);

        Assert.Equal(encrypted, reProtected);
    }

    // ------------------------------------------------------------------ //
    //  F) TryUnprotect on plaintext returns true + original value         //
    // ------------------------------------------------------------------ //

    [Fact]
    public void TryUnprotect_Plaintext_ReturnsTrueAndOriginalValue()
    {
        var svc = CreateService();

        var ok = svc.TryUnprotect("plain-text-key", out var result);

        Assert.True(ok);
        Assert.Equal("plain-text-key", result);
    }

    // ------------------------------------------------------------------ helpers --//

    private static ApiKeyProtectionService CreateService()
    {
        var provider = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
        return new ApiKeyProtectionService(provider, NullLogger<ApiKeyProtectionService>.Instance);
    }
}
