namespace SmartSwitch.Core.Models;

public static class MigrationPackageSchema
{
    public const int CurrentVersion = 1;
    public const string ManifestEntryName = "manifest.json";
}

public enum PackageContentKind
{
    UserFile,
    PortableApplicationFile,
}

public sealed record PackageBlobDescriptor(
    string BlobPath,
    string Sha256,
    long Length);

public sealed record PackageFileEntry(
    string ModuleId,
    PackageContentKind ContentKind,
    string DestinationPath,
    DateTimeOffset LastWriteTimeUtc,
    PackageBlobDescriptor Blob);

public sealed record PackageApplicationEntry(
    string Id,
    string DisplayName,
    string? Version,
    string? Publisher,
    ApplicationTransferability Transferability,
    string TransferabilityReason,
    IReadOnlyList<string> Dependencies,
    string? SilentInstallArguments);

public sealed record PackageSignature(
    string Algorithm,
    string PublicKey,
    string Value);

public sealed record SystemSettingsSnapshot(
    string Culture,
    string UiCulture,
    string TimeZoneId,
    bool? UseLightTheme,
    bool? ShowHiddenFiles,
    bool? ShowFileExtensions);

public sealed record NetworkAdapterSnapshot(
    string Name,
    string Description,
    string AdapterType,
    bool IsDhcpEnabled,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers);

public sealed record NetworkSettingsSnapshot(
    IReadOnlyList<NetworkAdapterSnapshot> Adapters);

public sealed record MigrationPackageManifest(
    int SchemaVersion,
    Guid SessionId,
    DateTimeOffset CreatedAtUtc,
    string DonorComputerName,
    string DonorOperatingSystem,
    string DonorOsArchitecture,
    int DonorWindowsBuild,
    bool RequiresPreOs,
    IReadOnlyList<PackageFileEntry> Files,
    IReadOnlyList<PackageApplicationEntry> Applications,
    SystemSettingsSnapshot? SystemSettings,
    NetworkSettingsSnapshot? NetworkSettings,
    long TotalUncompressedBytes,
    PackageSignature? Signature);

public sealed record MigrationPackageBuildRequest(
    Guid SessionId,
    MigrationScanSummary FileScan,
    IReadOnlyList<InstalledApplication> SelectedApplications,
    bool IncludeSystemSettings,
    bool IncludeNetworkSettings,
    bool Compress,
    bool Sign,
    string OutputDirectory);

public sealed record PreparedMigrationPackage(
    Guid SessionId,
    string PackagePath,
    long PackageLength,
    string Sha256,
    MigrationPackageManifest Manifest,
    IReadOnlyList<string> Warnings);

public sealed record PackageValidationResult(
    bool IsValid,
    MigrationPackageManifest? Manifest,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);
