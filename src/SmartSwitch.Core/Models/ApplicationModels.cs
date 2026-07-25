namespace SmartSwitch.Core.Models;

public enum ApplicationTransferability
{
    PortableCopy,
    ReinstallPlan,
    NotTransferable,
}

public enum ApplicationInstallScope
{
    CurrentUser,
    AllUsers,
    Unknown,
}

public sealed record InstalledApplication(
    string Id,
    string DisplayName,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    long? EstimatedSizeBytes,
    string Architecture,
    ApplicationInstallScope InstallScope,
    ApplicationTransferability Transferability,
    string TransferabilityReason,
    IReadOnlyList<string> Dependencies,
    string? InstallerSource,
    string? SilentInstallArguments,
    DateTimeOffset? InstallDateUtc)
{
    public bool IsSelectable => Transferability != ApplicationTransferability.NotTransferable;
}

public sealed record ApplicationInventoryResult(
    IReadOnlyList<InstalledApplication> Applications,
    IReadOnlyList<string> Warnings,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);
