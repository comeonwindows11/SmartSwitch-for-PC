namespace SmartSwitch.Core.Models;

public enum CompatibilityIssueSeverity
{
    Information,
    Warning,
    Blocking,
}

public sealed record SystemCompatibilitySnapshot(
    string ComputerName,
    string OperatingSystem,
    Version OperatingSystemVersion,
    string ProcessArchitecture,
    string OsArchitecture,
    long AvailableStorageBytes,
    bool IsAdministrator,
    bool Is64BitOperatingSystem);

public sealed record CompatibilityIssue(
    string Code,
    CompatibilityIssueSeverity Severity,
    string Message);

public sealed record CompatibilityReport(
    SystemCompatibilitySnapshot Receiver,
    IReadOnlyList<CompatibilityIssue> Issues)
{
    public bool IsCompatible =>
        Issues.All(issue => issue.Severity != CompatibilityIssueSeverity.Blocking);
}

public sealed record TransferPreflightMetadata(
    int PackageSchemaVersion,
    string DonorOperatingSystem,
    string DonorOsArchitecture,
    int DonorWindowsBuild,
    bool RequiresPreOs,
    long RequiredStagingBytes);
