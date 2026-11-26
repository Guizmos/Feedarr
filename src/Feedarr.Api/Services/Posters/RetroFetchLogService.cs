using System.Text;
using Feedarr.Api.Options;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Posters;

public sealed record RetroFetchLogEntry(
    string? Category,
    string? MediaType,
    string? Provider,
    string? Query,
    string? Reason,
    long? ReleaseId = null,
    DateTimeOffset? AttemptedAtUtc = null,
    string? Strategy = null,
    string? Coverage = null,
    string? Stage = null,
    string? Ranking = null);

public sealed class RetroFetchLogService
{
    private readonly AppOptions _opt;
    private readonly object _writeLock = new();
    private readonly IWebHostEnvironment _env;

    public RetroFetchLogService(IOptions<AppOptions> opt, IWebHostEnvironment env)
    {
        _opt = opt.Value;
        _env = env;
    }

    private string DataDirAbs =>
        Path.IsPathRooted(_opt.DataDir)
            ? _opt.DataDir
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, _opt.DataDir));

    public string LogsDirPath => Path.Combine(DataDirAbs, "logs");

    public string CreateRetroFetchLog()
    {
        Directory.CreateDirectory(LogsDirPath);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var file = $"retro-fetch-{stamp}-{suffix}.csv";
        var full = Path.Combine(LogsDirPath, file);

        var header = "category,mediaType,provider,query,reason,releaseId,attemptedAtUtc,strategy,coverage,stage,ranking" + Environment.NewLine;
        File.WriteAllText(full, header, Encoding.UTF8);

        return file;
    }

    public void AppendFailure(string logFile, RetroFetchLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(logFile)) return;
        try
        {
            Directory.CreateDirectory(LogsDirPath);

            var safeFile = Path.GetFileName(logFile);
            if (string.IsNullOrWhiteSpace(safeFile)) return;

            var full = Path.Combine(LogsDirPath, safeFile);

            lock (_writeLock)
            {
                var fields = new List<string?> {
                    entry.Category, entry.MediaType, entry.Provider, entry.Query, entry.Reason };
                // A job queued before an upgrade may still point at a five-column CSV.
                using (var reader = new StreamReader(full))
                    if (reader.ReadLine()?.Contains("releaseId", StringComparison.Ordinal) == true)
                        fields.AddRange(new[] { entry.ReleaseId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            entry.AttemptedAtUtc?.ToUniversalTime().ToString("O"), entry.Strategy, entry.Coverage, entry.Stage, entry.Ranking });
                var line = string.Join(",", fields.Select(EscapeCsv));

                File.AppendAllText(full, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // ignore logging failures
        }
    }

    /// <summary>Deletes all retro-fetch CSV log files from the logs directory.</summary>
    public int PurgeLogFiles()
    {
        var dir = LogsDirPath;
        if (!Directory.Exists(dir)) return 0;

        var deleted = 0;
        foreach (var file in Directory.GetFiles(dir, "retro-fetch-*.csv", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch { }
        }
        return deleted;
    }

    public string? ResolveLogPath(string logFile)
    {
        if (string.IsNullOrWhiteSpace(logFile)) return null;
        var safeFile = Path.GetFileName(logFile);
        if (string.IsNullOrWhiteSpace(safeFile)) return null;
        if (!safeFile.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(LogsDirPath, safeFile);
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var safe = System.Text.RegularExpressions.Regex.Replace(value, @"(?i)https?://[^\s""<>]+|\bbearer\s+[^\s""<>]+|\b(?:api[_-]?key|passkey|token|password)[""']?\s*[=:]\s*[""']?[^\s,;""<>]+", "[redacted]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var sanitized = safe.Replace("\r", " ").Replace("\n", " ");
        if (sanitized.Contains('"'))
            sanitized = sanitized.Replace("\"", "\"\"");
        if (sanitized.Contains(',') || sanitized.Contains('"'))
            return $"\"{sanitized}\"";
        return sanitized;
    }
}
