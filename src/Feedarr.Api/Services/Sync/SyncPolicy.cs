namespace Feedarr.Api.Services.Sync;

public enum PosterSelectionMode
{
    Seeds = 0,
    Ids = 1,
}

public abstract record SyncPolicy
{
    public abstract string Name { get; }
    public abstract string LogPrefix { get; }
    public abstract int MinPerCategoryLimit { get; }
    public abstract int MaxPerCategoryLimit { get; }
    public abstract int MinGlobalLimit { get; }
    public abstract int MaxGlobalLimit { get; }

    public virtual bool AllowSearchInitial => false;
    public virtual bool EnableCategoryFallback => true;
    public virtual bool RecordIndexerQuery => false;
    public virtual bool RecordPerSourceSyncJob => false;
    public virtual bool EmitCategoryDebugActivity => false;
    public virtual bool RequireEnabledSource => true;
    public virtual PosterSelectionMode PosterSelectionMode => PosterSelectionMode.Seeds;

    public virtual string BuildSuccessActivityMessage(string sourceName, int itemsCount, string syncMode)
        => $"Sync OK ({itemsCount} items, mode={syncMode})";

    public virtual string BuildErrorActivityMessage(string sourceName, string safeError)
        => $"Sync ERROR: {safeError}";
}

public sealed record AutoSyncPolicy : SyncPolicy
{
    public override string Name => "auto";
    public override string LogPrefix => "AutoSync";
    public override int MinPerCategoryLimit => 1;
    public override int MaxPerCategoryLimit => 200;
    public override int MinGlobalLimit => 1;
    public override int MaxGlobalLimit => 2000;
    public override bool RecordIndexerQuery => true;

    public override string BuildSuccessActivityMessage(string sourceName, int itemsCount, string syncMode)
        => $"AutoSync OK [{sourceName}] ({itemsCount} items, mode={syncMode})";

    public override string BuildErrorActivityMessage(string sourceName, string safeError)
        => $"AutoSync ERROR [{sourceName}]: {safeError}";
}

public sealed record ManualSyncPolicy : SyncPolicy
{
    public override string Name => "manual";
    public override string LogPrefix => "ManualSync";
    public override int MinPerCategoryLimit => 10;
    public override int MaxPerCategoryLimit => 500;
    public override int MinGlobalLimit => 50;
    public override int MaxGlobalLimit => 2000;
    public override bool RecordPerSourceSyncJob => true;
    public override bool EmitCategoryDebugActivity => true;
    public override bool RequireEnabledSource => false;
}

public sealed record SchedulerSyncPolicy : SyncPolicy
{
    public override string Name => "scheduler";
    public override string LogPrefix => "SchedulerSync";
    public override int MinPerCategoryLimit => 1;
    public override int MaxPerCategoryLimit => 200;
    public override int MinGlobalLimit => 1;
    public override int MaxGlobalLimit => 2000;

    public override string BuildSuccessActivityMessage(string sourceName, int itemsCount, string syncMode)
        => $"Manual Run OK ({itemsCount} items, mode={syncMode})";

    public override string BuildErrorActivityMessage(string sourceName, string safeError)
        => $"Manual Run ERROR: {safeError}";
}
