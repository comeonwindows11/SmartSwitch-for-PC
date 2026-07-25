namespace SmartSwitch.Core.Models;

public enum PreOsCapabilityState
{
    Ready,
    AgentMissing,
    MediaMissing,
    AdministratorRequired,
    UnsupportedPlatform,
}

public sealed record PreOsCapability(
    PreOsCapabilityState State,
    string Message,
    string? AgentPath,
    string? BootWimPath,
    string? BootSdiPath)
{
    public bool IsReady => State == PreOsCapabilityState.Ready;
}

public sealed record PreOsJob(
    Guid JobId,
    Guid SessionId,
    string PackagePath,
    string TargetRoot,
    string JobDirectory,
    DateTimeOffset CreatedAtUtc,
    string? BootEntryId = null);

public enum PreOsApplyState
{
    Pending,
    Staged,
    BackedUp,
    Replaced,
    Verified,
    Committed,
    RolledBack,
    Failed,
}

public sealed record PreOsApplyJournalEntry(
    string DestinationPath,
    string BlobSha256,
    PreOsApplyState State,
    string? BackupPath,
    string? Error);

public sealed record PreOsApplyJournal(
    Guid JobId,
    Guid SessionId,
    DateTimeOffset UpdatedAtUtc,
    bool Completed,
    IReadOnlyList<PreOsApplyJournalEntry> Entries);

public sealed record PreOsApplyResult(
    bool Succeeded,
    int AppliedFileCount,
    int DeferredApplicationCount,
    string JournalPath,
    string LogPath,
    IReadOnlyList<string> Warnings);
