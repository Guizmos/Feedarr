using System.Text;
using Feedarr.Api.Options;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Posters;

public sealed record RetroFetchLogEntry(
    string? Category,
    string? MediaType,
    string? Provider,
    string? Query,
    string? Reason);

public sealed class RetroFetchLogService
{
    private readonly AppOptions _opt;
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

        var header = "category,mediaType,provider,query,reason" + Environment.NewLine;
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

            var line = string.Join(",",
                EscapeCsv(entry.Category),
                EscapeCsv(entry.MediaType),
                EscapeCsv(entry.Provider),
                EscapeCsv(entry.Query),
                EscapeCsv(entry.Reason)
            );

            File.AppendAllText(full, line + Environment.NewLine, Encoding.UTF8);
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
        var sanitized = value.Replace("\r", " ").Replace("\n", " ");
        if (sanitized.Contains('"'))
            sanitized = sanitized.Replace("\"", "\"\"");
        if (sanitized.Contains(',') || sanitized.Contains('"'))
            return $"\"{sanitized}\"";
        return sanitized;
    }
}
