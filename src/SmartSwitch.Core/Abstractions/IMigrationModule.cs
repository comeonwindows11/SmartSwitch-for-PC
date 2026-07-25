using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Abstractions;

public interface IMigrationModule
{
    string Id { get; }

    string DisplayName { get; }

    IReadOnlyCollection<string> Dependencies { get; }

    IReadOnlyCollection<MigrationCategory> SupportedCategories { get; }

    Task<ModuleScanResult> ScanAsync(
        MigrationRequest request,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken);
}
