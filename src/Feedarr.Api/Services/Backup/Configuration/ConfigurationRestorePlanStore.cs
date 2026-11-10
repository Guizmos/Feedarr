using System.Security.Cryptography;
using System.Text.Json;
using Feedarr.Api.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Feedarr.Api.Services.Backup.Configuration;

/// <summary>Locally authenticated staging, outside all poster/backup GC roots.</summary>
public sealed class ConfigurationRestorePlanStore
{
    private const int Limit = 32 * 1024 * 1024;
    private readonly IDataProtector _protector;
    private readonly string _root;
    private string PlanPath => Path.Combine(_root, "plan.bin");
    private bool _confirmed;
    public bool BlocksSync => Volatile.Read(ref _confirmed);
    internal static string Root(string data) => Path.Combine(data, "configuration-restore");
    internal static bool HasPlan(string data) => File.Exists(Path.Combine(Root(data), "plan.bin")) ||
        Directory.Exists(Path.Combine(Root(data), "plan.bin")) || File.Exists(Path.Combine(Root(data), "plan.bin.tmp")) || Directory.Exists(Path.Combine(Root(data), "plan.bin.tmp"));

    public ConfigurationRestorePlanStore(Db db, IDataProtectionProvider provider)
    {
        _root = Root(Path.GetDirectoryName(Path.GetFullPath(db.DbPath))!);
        _protector = provider.CreateProtector("Feedarr.ConfigurationRestorePlan.v1");
        if (File.Exists(PlanPath)) Read();
    }

    internal static void EnsureNoLegacyConflict(string data)
    {
        if (!HasPlan(data)) return;
        var legacy = Path.Combine(data, "restore-state");
        if (File.Exists(Path.Combine(legacy, "pending.json")) || File.Exists(Path.Combine(legacy, "operation.json")))
            throw Conflict(); // fail before legacy SQLite/WAL swaps, DI and workers
    }

    internal ConfigurationRestorePlan? Read()
    {
        EnsureSafePaths();
        if (!File.Exists(PlanPath))
        {
            if (HasPlan(Path.GetDirectoryName(_root)!)) throw Conflict();
            return null;
        }
        byte[]? plaintext = null;
        try
        {
            if (new FileInfo(PlanPath).Length > Limit) throw Conflict();
            plaintext = _protector.Unprotect(File.ReadAllBytes(PlanPath));
            var plan = JsonSerializer.Deserialize<ConfigurationRestorePlan>(plaintext, ConfigurationSchema.Json) ?? throw Conflict();
            if (!Guid.TryParseExact(plan.Id, "N", out _) || plan.Version != 1) throw Conflict();
            Volatile.Write(ref _confirmed, plan.Confirmed);
            return plan;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        { throw Conflict(); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal void Write(ConfigurationRestorePlan plan)
    {
        EnsureSafePaths();
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, ConfigurationSchema.Json);
        try
        {
            if (bytes.Length > Limit / 2) throw Conflict();
            var encrypted = _protector.Protect(bytes);
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(PlanPath + ".tmp", options)) { file.Write(encrypted); file.Flush(true); }
            File.Move(PlanPath + ".tmp", PlanPath, true);
            Volatile.Write(ref _confirmed, plan.Confirmed);
            PendingRestoreStore.SyncDirectory(_root);
            PendingRestoreStore.SyncDirectory(Path.GetDirectoryName(_root)!);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal void Clear()
    {
        EnsureSafePaths();
        File.Delete(PlanPath);
        File.Delete(PlanPath + ".tmp");
        PendingRestoreStore.SyncDirectory(_root);
        Volatile.Write(ref _confirmed, false);
    }
    private void EnsureSafePaths()
    {
        foreach (var path in new[] { _root, PlanPath, PlanPath + ".tmp" })
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw Conflict();
    }
    internal static BackupOperationException Conflict() => new("configuration_restore_conflict_or_unreadable_state", 409);
}

public sealed record ConfigurationRestorePreview(string OperationId, bool FreshInstallation, bool HasLibrary,
    int MatchedSources, int NewSources, int DisabledHistoricalSources, bool StricterRetention,
    bool SecurityChangeRequiresConfirmation, bool CanCompleteSetup, ConfigurationPreview Configuration);

internal sealed class ConfigurationRestorePlan
{
    public int Version { get; set; } = 1;
    public required string Id { get; init; }
    public bool Confirmed { get; set; }
    public required string Baseline { get; init; }
    public required ConfigurationDocument Configuration { get; init; }
    public required Dictionary<string, long?> Providers { get; init; }
    public required Dictionary<string, long?> Sources { get; init; }
    public required Dictionary<string, long?> Applications { get; init; }
    public required Dictionary<string, string?> ExternalProviders { get; init; }
    public required ConfigurationRestorePreview Preview { get; init; }
    public int PreviousPerCategory { get; init; }
    public int PreviousGlobal { get; init; }
}
