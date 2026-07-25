using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace SmartSwitch.Setup.Services;

public sealed class InstallerService
{
    private const string PayloadResourceName = "SmartSwitch.Setup.Payload.zip";
    private const string ManifestFileName = ".smartswitch-install.json";
    private const string UninstallerFileName = "Uninstall SmartSwitch.exe";
    private const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SmartSwitchMigrationTool";
    private const uint MoveFileDelayUntilReboot = 0x00000004;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
    };

    public string GetDefaultInstallationPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "SmartSwitch Migration Tool");

    public async Task InstallAsync(
        string installationPath,
        bool createDesktopShortcut,
        IProgress<InstallerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destinationRoot = ValidateInstallationPath(installationPath);
        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            $"SmartSwitch.Setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        try
        {
            progress?.Report(new InstallerProgress(5, "Extraction des composants…"));
            await ExtractPayloadAsync(stagingRoot, cancellationToken);
            var payloadFiles = Directory
                .EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
                .ToArray();
            if (payloadFiles.Length == 0)
            {
                throw new InvalidDataException("Le paquet d'installation ne contient aucun fichier.");
            }

            Directory.CreateDirectory(destinationRoot);
            var installedFiles = new List<string>(payloadFiles.Length + 2);
            for (var index = 0; index < payloadFiles.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = payloadFiles[index];
                var relativePath = Path.GetRelativePath(stagingRoot, sourcePath);
                var destinationPath = GetSafeChildPath(destinationRoot, relativePath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destinationPath) ?? destinationRoot);
                await CopyFileAsync(sourcePath, destinationPath, cancellationToken);
                installedFiles.Add(relativePath);
                progress?.Report(new InstallerProgress(
                    10 + (index + 1) * 65d / payloadFiles.Length,
                    $"Copie de {Path.GetFileName(relativePath)}…"));
            }

            var setupExecutable = Environment.ProcessPath ??
                throw new InvalidOperationException(
                    "Le chemin de l'assistant d'installation est indisponible.");
            var uninstallerPath = Path.Combine(destinationRoot, UninstallerFileName);
            await CopyFileAsync(setupExecutable, uninstallerPath, cancellationToken);
            installedFiles.Add(UninstallerFileName);

            progress?.Report(new InstallerProgress(82, "Création des raccourcis…"));
            var shortcuts = CreateShortcuts(destinationRoot, createDesktopShortcut);
            var manifest = new InstallManifest("0.1.0", installedFiles, shortcuts);
            var manifestPath = Path.Combine(destinationRoot, ManifestFileName);
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, ManifestJsonOptions),
                cancellationToken);

            progress?.Report(new InstallerProgress(92, "Inscription dans Windows…"));
            WriteUninstallRegistration(destinationRoot, uninstallerPath);
            progress?.Report(new InstallerProgress(100, "Installation terminée."));
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Impossible de remplacer un composant. Fermez SmartSwitch puis réessayez.",
                exception);
        }
        finally
        {
            DeleteStagingDirectory(stagingRoot);
        }
    }

    public async Task UninstallAsync(
        IProgress<InstallerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installationPath = ReadInstallationPath();
        if (installationPath is null)
        {
            throw new InvalidOperationException(
                "Aucune installation SmartSwitch enregistrée n'a été trouvée.");
        }

        var destinationRoot = ValidateInstallationPath(installationPath);
        var manifestPath = Path.Combine(destinationRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                "Le manifeste d'installation est absent; la désinstallation automatique est interrompue.");
        }

        var manifest = JsonSerializer.Deserialize<InstallManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken))
            ?? throw new InvalidDataException("Le manifeste d'installation est invalide.");
        var totalSteps = Math.Max(1, manifest.Files.Count + manifest.Shortcuts.Count);
        var completedSteps = 0;

        foreach (var shortcut in manifest.Shortcuts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsKnownShortcutPath(shortcut) && File.Exists(shortcut))
            {
                File.Delete(shortcut);
            }

            completedSteps++;
            progress?.Report(new InstallerProgress(
                completedSteps * 85d / totalSteps,
                "Suppression des raccourcis…"));
        }

        foreach (var relativePath in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = GetSafeChildPath(destinationRoot, relativePath);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            completedSteps++;
            progress?.Report(new InstallerProgress(
                completedSteps * 85d / totalSteps,
                $"Suppression de {Path.GetFileName(relativePath)}…"));
        }

        File.Delete(manifestPath);
        RemoveUninstallRegistration();
        DeleteEmptyDirectories(destinationRoot);
        progress?.Report(new InstallerProgress(100, "Désinstallation terminée."));

        var runningExecutable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(runningExecutable) &&
            Path.GetFileName(runningExecutable).StartsWith(
                "SmartSwitch-Uninstall-",
                StringComparison.OrdinalIgnoreCase))
        {
            _ = MoveFileEx(runningExecutable, null, MoveFileDelayUntilReboot);
        }
    }

    private static async Task ExtractPayloadAsync(
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        await using var resourceStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException(
                "Le contenu de SmartSwitch est absent. Reconstruisez l'assistant avec build\\Build-Installer.ps1.");
        using var archive = new ZipArchive(resourceStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = GetSafeChildPath(stagingRoot, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath) ?? stagingRoot);
            await using var input = entry.Open();
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81_920,
                FileOptions.Asynchronous);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static List<string> CreateShortcuts(
        string installationRoot,
        bool createDesktopShortcut)
    {
        var executablePath = Path.Combine(installationRoot, "SmartSwitch.exe");
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "L'exécutable SmartSwitch est absent du paquet.",
                executablePath);
        }

        var shortcuts = new List<string>();
        var programsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "SmartSwitch Migration Tool");
        Directory.CreateDirectory(programsFolder);
        var startMenuShortcut = Path.Combine(
            programsFolder,
            "SmartSwitch Migration Tool.lnk");
        ShortcutService.Create(
            startMenuShortcut,
            executablePath,
            installationRoot,
            "Migration sécurisée de PC à PC");
        shortcuts.Add(startMenuShortcut);

        if (createDesktopShortcut)
        {
            var desktopShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "SmartSwitch Migration Tool.lnk");
            ShortcutService.Create(
                desktopShortcut,
                executablePath,
                installationRoot,
                "Migration sécurisée de PC à PC");
            shortcuts.Add(desktopShortcut);
        }

        return shortcuts;
    }

    private static void WriteUninstallRegistration(
        string installationRoot,
        string uninstallerPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, writable: true);
        key.SetValue("DisplayName", "SmartSwitch Migration Tool");
        key.SetValue("DisplayVersion", "0.1.0");
        key.SetValue("Publisher", "SmartSwitch Contributors");
        key.SetValue("InstallLocation", installationRoot);
        key.SetValue("DisplayIcon", Path.Combine(installationRoot, "SmartSwitch.exe"));
        key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue(
            "InstallDate",
            DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
    }

    private static string? ReadInstallationPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath);
        return key?.GetValue("InstallLocation") as string;
    }

    private static void RemoveUninstallRegistration()
    {
        Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
    }

    private static string ValidateInstallationPath(string installationPath)
    {
        if (string.IsNullOrWhiteSpace(installationPath))
        {
            throw new ArgumentException("Choisissez un dossier d'installation.");
        }

        var fullPath = Path.GetFullPath(installationPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "La racine d'un lecteur ne peut pas servir de dossier d'installation.");
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (fullPath.StartsWith(
                windowsDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Le dossier Windows ne peut pas servir de dossier d'installation.");
        }

        return fullPath;
    }

    private static string GetSafeChildPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Le paquet contient un chemin invalide.");
        }

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!candidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Une tentative de sortie du dossier d'installation a été bloquée.");
        }

        return candidate;
    }

    private static bool IsKnownShortcutPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var knownRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        return knownRoots.Any(root =>
            fullPath.StartsWith(
                Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    private static void DeleteStagingDirectory(string stagingRoot)
    {
        var fullPath = Path.GetFullPath(stagingRoot);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith(
                "SmartSwitch.Setup-",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteEmptyDirectories(string installationRoot)
    {
        if (!Directory.Exists(installationRoot))
        {
            return;
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(installationRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(installationRoot).Any())
        {
            Directory.Delete(installationRoot);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string? newFileName,
        uint flags);
}
