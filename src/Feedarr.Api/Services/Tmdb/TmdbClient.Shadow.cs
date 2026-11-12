using System.Text.Json;
using System.Threading;

namespace Feedarr.Api.Services.Tmdb;

public sealed partial class TmdbClient
{
    private readonly AsyncLocal<MatchingShadowCapture?> _shadowCapture = new();

    internal sealed class MatchingShadowCapture(Action dispose) : IDisposable
    {
        internal int ExpectedResponses { get; private set; }
        internal int ReceivedResponses { get; private set; }
        internal bool Complete { get; private set; } = true;
        internal void Expect() => ExpectedResponses += 2; // Same default + UI language requests as historical code.
        internal void Observe(bool complete) { ReceivedResponses++; Complete &= complete; }
        internal bool IsComplete => ExpectedResponses > 0 && ExpectedResponses == ReceivedResponses && Complete;
        public void Dispose() => dispose();
    }

    internal MatchingShadowCapture BeginMatchingShadowCapture()
    {
        var previous = _shadowCapture.Value;
        var capture = new MatchingShadowCapture(() => _shadowCapture.Value = previous);
        _shadowCapture.Value = capture;
        return capture;
    }

    // Metadata is observational only: never change return values, requests or error behavior.
    private void ObserveShadowResponse(SearchResponse? response, int limit)
    {
        var capture = _shadowCapture.Value;
        if (capture is null) return;
        static int? Number(JsonElement? element) => element is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var n) ? n : null;
        var complete = response?.Results is { } rows && Number(response.Page) == 1 &&
            Number(response.TotalPages) is 0 or 1 && Number(response.TotalResults) == rows.Count &&
            rows.Count <= Math.Clamp(limit <= 0 ? 10 : limit, 1, 50) &&
            rows.All(r => r.Id > 0 && !string.IsNullOrWhiteSpace(r.Title ?? r.Name));
        capture.Observe(complete);
    }
}
