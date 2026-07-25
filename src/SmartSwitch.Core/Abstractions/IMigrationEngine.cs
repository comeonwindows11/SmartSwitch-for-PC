using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Abstractions;

public interface IMigrationEngine
{
    IReadOnlyCollection<IMigrationModule> Modules { get; }

    Task<MigrationScanSummary> ScanAsync(
        MigrationRequest request,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
