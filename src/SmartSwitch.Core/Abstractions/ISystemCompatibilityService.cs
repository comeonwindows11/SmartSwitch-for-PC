using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Abstractions;

public interface ISystemCompatibilityService
{
    Task<SystemCompatibilitySnapshot> CaptureAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    Task<CompatibilityReport> ValidateAsync(
        MigrationPackageManifest manifest,
        string storagePath,
        CancellationToken cancellationToken = default);
}
