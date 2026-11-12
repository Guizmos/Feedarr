using System.Text.Json;
using System.Threading;

namespace Feedarr.Api.Services.Tmdb;

public sealed partial class TmdbClient
{
    private readonly AsyncLocal<MatchingShadowCapture?> _shadowCapture = new();

    internal sealed record ObservedSearchResult(SearchResult Result, string? ResponseLanguage);

    internal sealed class MatchingShadowCapture(Action dispose) : IDisposable
    {
        private readonly List<ObservedSearchResult> _results = new();
        internal IReadOnlyList<ObservedSearchResult> Results => _results;
        internal void Add(ObservedSearchResult result)
        {
            if (_results.Count >= 100) { Complete = false; return; }
            _results.Add(result);
        }
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
    private void ObserveShadowResponse(SearchResponse? response, int limit, string type, string? responseLanguage)
    {
        var capture = _shadowCapture.Value;
        if (capture is null) return;
        static int? Number(JsonElement? element) => element is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var n) ? n : null;
        var complete = response?.Results is { } rows && Number(response.Page) == 1 &&
            Number(response.TotalPages) is 0 or 1 && Number(response.TotalResults) == rows.Count &&
            rows.Count <= Math.Clamp(limit <= 0 ? 10 : limit, 1, 50) &&
            rows.All(r => r.Id > 0 && !string.IsNullOrWhiteSpace(r.Title ?? r.Name));
        capture.Observe(complete);
        if (response?.Results is not { } items) return;
        // Observe before historical de-duplication: the later localized row may otherwise disappear.
        foreach (var row in items.Take(Math.Clamp(limit <= 0 ? 10 : limit, 1, 50)))
        {
            if (row.Id <= 0) continue;
            var title = type == "movie" ? row.Title ?? row.Name : row.Name ?? row.Title;
            var original = type == "movie" ? row.OriginalTitle ?? row.OriginalName : row.OriginalName ?? row.OriginalTitle;
            if (string.IsNullOrWhiteSpace(title)) continue;
            capture.Add(new(new(row.Id, title.Trim(), original?.Trim(), null, type,
                ExtractYear(type == "movie" ? row.ReleaseDate : row.FirstAirDate), row.OriginalLanguage), responseLanguage));
        }
    }
}
