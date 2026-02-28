using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Tests;

public sealed class SensitiveUrlSanitizerTests
{
    [Fact]
    public void Sanitize_MasksSensitiveToken()
    {
        var sanitized = SensitiveUrlSanitizer.Sanitize("https://feedarr.example/api?t=search&token=abc123");

        Assert.Equal("https://feedarr.example/api?t=search&token=***", sanitized);
    }

    [Fact]
    public void Sanitize_MasksMixedCaseApiKey()
    {
        var sanitized = SensitiveUrlSanitizer.Sanitize("https://feedarr.example/api?ApiKey=secret&limit=10");

        Assert.Equal("https://feedarr.example/api?ApiKey=***&limit=10", sanitized);
    }

    [Fact]
    public void Sanitize_LeavesUrlWithoutQueryUnchanged()
    {
        const string url = "https://feedarr.example/api";
        Assert.Equal(url, SensitiveUrlSanitizer.Sanitize(url));
    }
}
