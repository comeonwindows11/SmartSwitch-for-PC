using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Abstractions;

public interface IPreOsService
{
    Task<PreOsCapability> GetCapabilityAsync(
        CancellationToken cancellationToken = default);

    Task<PreOsJob> PrepareJobAsync(
        Guid sessionId,
        string packagePath,
        string targetRoot,
        CancellationToken cancellationToken = default);

    Task ScheduleAndRestartAsync(
        PreOsJob job,
        CancellationToken cancellationToken = default);
}

public interface IPreOsPackageApplier
{
    Task<PreOsApplyResult> ApplyAsync(
        PreOsJob job,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
