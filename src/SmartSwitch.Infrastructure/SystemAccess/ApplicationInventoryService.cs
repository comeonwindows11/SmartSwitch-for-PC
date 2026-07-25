using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Infrastructure.SystemAccess;

public sealed class ApplicationInventoryService : IApplicationInventoryService
{
    private static readonly string[] UnsafeNameFragments =
    [
        "antivirus",
        "security",
        "driver",
        "firmware",
        "hotfix",
        "update for",
        "oem",
    ];

    public Task<ApplicationInventoryResult> ScanAsync(
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Scan(progress, cancellationToken),
            cancellationToken);

    private static ApplicationInventoryResult Scan(
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var applications = new List<InstalledApplication>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locations = new (RegistryHive Hive, RegistryView View, ApplicationInstallScope Scope)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, ApplicationInstallScope.AllUsers),
            (RegistryHive.LocalMachine, RegistryView.Registry32, ApplicationInstallScope.AllUsers),
            (RegistryHive.CurrentUser, RegistryView.Registry64, ApplicationInstallScope.CurrentUser),
            (RegistryHive.CurrentUser, RegistryView.Registry32, ApplicationInstallScope.CurrentUser),
        };

        foreach (var (hive, view, scope) in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(
                    @"SOFTWAREMicrosoftWindowsCurrentVersionUninstall");
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var key = uninstallKey.OpenSubKey(subKeyName);
                        if (key is null)
                        {
                            continue;
                        }

                        var displayName = ReadString(key, "DisplayName");
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            continue;
                        }

                        var version = ReadString(key, "DisplayVersion");
                        var publisher = ReadString(key, "Publisher");
                        var deduplicationKey =
                            $"{displayName}|{version}|{publisher}|{scope}";
                        if (!seen.Add(deduplicationKey))
                        {
                            continue;
                        }

                        var installLocation = NormalizeExistingDirectory(
                            ReadString(key, "InstallLocation"));
                        var uninstall = ReadString(key, "QuietUninstallString") ??
                            ReadString(key, "UninstallString");
                        var installerSource = NormalizeInstallerSource(
                            ReadString(key, "LocalPackage"));
                        var transferability = Classify(
                            displayName,
                            installLocation,
                            uninstall,
                            ReadDword(key, "SystemComponent") != 0,
                            ReadString(key, "ReleaseType"),
                            ReadString(key, "ParentKeyName"));
                        var architecture = view == RegistryView.Registry32 ? "x86" : "x64";
                        applications.Add(new InstalledApplication(
                            CreateId(hive, view, subKeyName),
                            displayName.Trim(),
                            version,
                            publisher,
                            installLocation,
                            ReadEstimatedSize(key),
                            architecture,
                            scope,
                            transferability.Value,
                            transferability.Reason,
                            DiscoverDependencies(installLocation),
                            installerSource,
                            GetSilentInstallArguments(uninstall),
                            ParseInstallDate(ReadString(key, "InstallDate"))));

                        if (applications.Count % 25 == 0)
                        {
                            progress?.Report(new MigrationProgress(
                                "Applications",
                                $"{applications.Count:N0} applications détectées…",
                                0,
                                CurrentItem: displayName));
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or SecurityException)
                    {
                        warnings.Add(
                            $"Impossible de lire une application installée ({view}): {exception.Message}");
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                warnings.Add($"Inventaire du registre {hive}/{view} incomplet: {exception.Message}");
            }
        }

        var ordered = applications
            .OrderBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        progress?.Report(new MigrationProgress(
            "Applications",
            $"{ordered.Length:N0} applications analysées.",
            100));
        return new ApplicationInventoryResult(
            ordered,
            warnings,
            startedAt,
            DateTimeOffset.UtcNow);
    }

    private static (ApplicationTransferability Value, string Reason) Classify(
        string displayName,
        string? installLocation,
        string? uninstall,
        bool isSystemComponent,
        string? releaseType,
        string? parentKeyName)
    {
        var normalizedName = displayName.ToLowerInvariant();
        if (isSystemComponent ||
            !string.IsNullOrWhiteSpace(parentKeyName) ||
            !string.IsNullOrWhiteSpace(releaseType) ||
            normalizedName.StartsWith("kb", StringComparison.OrdinalIgnoreCase) ||
            UnsafeNameFragments.Any(normalizedName.Contains))
        {
            return (
                ApplicationTransferability.NotTransferable,
                "Composant système, pilote, mise à jour ou logiciel sensible : non migré automatiquement.");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(installLocation) &&
            installLocation.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(uninstall))
        {
            return (
                ApplicationTransferability.PortableCopy,
                "Application portable détectée : les fichiers peuvent être copiés dans le package.");
        }

        return (
            ApplicationTransferability.ReinstallPlan,
            "Réinstallation requise après Windows : SmartSwitch transmet le plan, pas les binaires installés.");
    }

    private static IReadOnlyList<string> DiscoverDependencies(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
        {
            return [];
        }

        try
        {
            var files = Directory.EnumerateFiles(installLocation, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dependencies = new List<string>();
            AddIf(files, "vcruntime", "Microsoft Visual C++ Runtime", dependencies);
            AddIf(files, "msvcp", "Microsoft Visual C++ Runtime", dependencies);
            AddIf(files, "hostfxr", ".NET Runtime", dependencies);
            AddIf(files, "webview2", "Microsoft Edge WebView2 Runtime", dependencies);
            return dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception) when (true)
        {
            return [];
        }
    }

    private static void AddIf(
        IReadOnlyCollection<string?> files,
        string fragment,
        string dependency,
        ICollection<string> dependencies)
    {
        if (files.Any(file => file?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true))
        {
            dependencies.Add(dependency);
        }
    }

    private static string CreateId(RegistryHive hive, RegistryView view, string subKeyName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{hive}|{view}|{subKeyName}"));
        return Convert.ToHexString(bytes[..12]);
    }

    private static string? NormalizeExistingDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            ? Path.GetFullPath(path)
            : null;

    private static string? NormalizeInstallerSource(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? Path.GetFullPath(path)
            : null;

    private static string? ReadString(RegistryKey key, string valueName) =>
        key.GetValue(valueName) as string;

    private static int ReadDword(RegistryKey key, string valueName) =>
        key.GetValue(valueName) switch
        {
            int value => value,
            _ => 0,
        };

    private static long? ReadEstimatedSize(RegistryKey key)
    {
        try
        {
            return checked((long)ReadDword(key, "EstimatedSize") * 1024);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseInstallDate(string? value) =>
        DateTime.TryParseExact(
            value,
            "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal,
            out var parsed)
            ? new DateTimeOffset(parsed)
            : null;

    private static string? GetSilentInstallArguments(string? uninstall) =>
        string.IsNullOrWhiteSpace(uninstall)
            ? null
            : "/quiet";
}
