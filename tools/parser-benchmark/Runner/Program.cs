using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Titles;

if (args.Length != 3 || !int.TryParse(args[1], out var iterations) || iterations is < 1 or > 100 ||
    !int.TryParse(args[2], out var warmup) || warmup is < 1 or > 20)
    return 2;

using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
var entries = document.RootElement.GetProperty("entries").EnumerateArray().Select(row => new
{
    Id = row.GetProperty("id").GetString()!,
    Raw = row.GetProperty("raw").GetString()!,
    Category = Enum.Parse<UnifiedCategory>(row.GetProperty("category").GetString()!, ignoreCase: false)
}).ToList();
if (entries.Count == 0 || entries.Any(row => !Enum.IsDefined(row.Category)) ||
    entries.Select(row => row.Id).Distinct().Count() != entries.Count) return 2;

var parser = new TitleParser();
// JIT, static regex initialization and cold-start costs are excluded and disclosed.
for (var i = 0; i < warmup; i++)
    foreach (var row in entries) parser.Parse(row.Raw, row.Category);

var times = new List<double>();
var results = new List<object>();
var total = Stopwatch.StartNew();
foreach (var row in entries)
{
    ParsedTitle? parsed = null;
    var rowTimes = new List<double>();
    for (var i = 0; i < iterations; i++)
    {
        var start = Stopwatch.GetTimestamp();
        var current = parser.Parse(row.Raw, row.Category);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        rowTimes.Add(elapsed);
        // Check every structured output across repetitions, not just timing stability.
        if (parsed is not null && JsonSerializer.Serialize(parsed) != JsonSerializer.Serialize(current))
            throw new InvalidOperationException("Non-deterministic parser output");
        parsed = current;
    }
    rowTimes.Sort();
    times.AddRange(rowTimes);
    results.Add(new { id = row.Id, parsed, medianMicroseconds = rowTimes[rowTimes.Count / 2] });
}
total.Stop();
times.Sort();
Console.WriteLine(JsonSerializer.Serialize(new
{
    rows = results,
    performance = new
    {
        iterations, warmup, samples = times.Count,
        medianMicroseconds = times[times.Count / 2],
        p95Microseconds = times[Math.Max(0, (int)Math.Ceiling(times.Count * .95) - 1)],
        parseTotalMilliseconds = times.Sum() / 1000,
        loopWallMilliseconds = total.Elapsed.TotalMilliseconds,
        runtime = RuntimeInformation.FrameworkDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString()
    }
}));
return 0;
