using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Abstractions;

public interface IMigrationPackageService
{
    Task<PreparedMigrationPackage> BuildAsync(
        MigrationPackageBuildRequest request,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<PackageValidationResult> ValidateAsync(
        string packagePath,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<MigrationPackageManifest> ReadManifestAsync(
        string packagePath,
        CancellationToken cancellationToken = default);
}
