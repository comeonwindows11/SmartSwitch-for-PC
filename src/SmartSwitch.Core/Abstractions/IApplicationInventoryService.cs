using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Abstractions;

public interface IApplicationInventoryService
{
    Task<ApplicationInventoryResult> ScanAsync(
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
