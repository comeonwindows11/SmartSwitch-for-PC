using System.Runtime.InteropServices;
using System.Security.Principal;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Infrastructure.SystemAccess;

public sealed class SystemCompatibilityService : ISystemCompatibilityService
{
    public Task<SystemCompatibilitySnapshot> CaptureAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(storagePath);
        var root = Path.GetPathRoot(fullPath) ??
            throw new InvalidOperationException("Le volume de stockage est introuvable.");
        var drive = new DriveInfo(root);
        return Task.FromResult(new SystemCompatibilitySnapshot(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            Environment.OSVersion.Version,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.OSArchitecture.ToString(),
            drive.AvailableFreeSpace,
            IsAdministrator(),
            Environment.Is64BitOperatingSystem));
    }

    public async Task<CompatibilityReport> ValidateAsync(
        MigrationPackageManifest manifest,
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var receiver = await CaptureAsync(storagePath, cancellationToken);
        var issues = new List<CompatibilityIssue>();

        if (manifest.SchemaVersion != MigrationPackageSchema.CurrentVersion)
        {
            issues.Add(new CompatibilityIssue(
                "PackageSchema",
                CompatibilityIssueSeverity.Blocking,
                "La version du package n'est pas prise en charge par ce poste."));
        }

        if (!string.Equals(
                manifest.DonorOsArchitecture,
                receiver.OsArchitecture,
                StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new CompatibilityIssue(
                "Architecture",
                CompatibilityIssueSeverity.Blocking,
                "Les architectures système des deux PC ne sont pas compatibles."));
        }

        var requiredBytes = checked((long)Math.Ceiling(manifest.TotalUncompressedBytes * 1.25));
        if (receiver.AvailableStorageBytes < requiredBytes)
        {
            issues.Add(new CompatibilityIssue(
                "FreeStorage",
                CompatibilityIssueSeverity.Blocking,
                "L'espace disque disponible est insuffisant pour le package et son application."));
        }

        if (receiver.OperatingSystemVersion.Build < 19041)
        {
            issues.Add(new CompatibilityIssue(
                "WindowsBuild",
                CompatibilityIssueSeverity.Blocking,
                "Windows 10 version 2004 ou une version plus récente est requis."));
        }

        if (manifest.DonorWindowsBuild > receiver.OperatingSystemVersion.Build)
        {
            issues.Add(new CompatibilityIssue(
                "DonorNewer",
                CompatibilityIssueSeverity.Warning,
                "Le PC donneur utilise une build Windows plus récente : certains paramètres peuvent être ignorés."));
        }

        if (manifest.RequiresPreOs)
        {
            issues.Add(new CompatibilityIssue(
                "PreOs",
                CompatibilityIssueSeverity.Information,
                "L'application finale sera exécutée dans l'environnement pré-OS SmartSwitch."));
        }

        return new CompatibilityReport(receiver, issues);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
