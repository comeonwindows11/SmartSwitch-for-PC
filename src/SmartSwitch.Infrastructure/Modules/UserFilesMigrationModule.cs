using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Infrastructure.Modules;

public sealed class UserFilesMigrationModule : IMigrationModule
{
    private static readonly IReadOnlyDictionary<MigrationCategory, string> CategoryNames =
        new Dictionary<MigrationCategory, string>
        {
            [MigrationCategory.Desktop] = "Desktop",
            [MigrationCategory.Documents] = "Documents",
            [MigrationCategory.Downloads] = "Downloads",
            [MigrationCategory.Pictures] = "Pictures",
            [MigrationCategory.Music] = "Music",
            [MigrationCategory.Videos] = "Videos",
        };

    public string Id => "user-files";

    public string DisplayName => "Fichiers du profil utilisateur";

    public IReadOnlyCollection<string> Dependencies => [];

    public IReadOnlyCollection<MigrationCategory> SupportedCategories =>
        [.. CategoryNames.Keys, MigrationCategory.CustomFiles];

    public Task<ModuleScanResult> ScanAsync(
        MigrationRequest request,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => Scan(request, progress, cancellationToken), cancellationToken);

    private ModuleScanResult Scan(
        MigrationRequest request,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<MigrationFile>();
        var warnings = new List<string>();
        var roots = BuildRoots(request);
        var seenRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rootPath, destinationPrefix) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(rootPath))
            {
                warnings.Add($"Le dossier « {rootPath} » n'existe pas et a été ignoré.");
                continue;
            }

            EnumerateRoot(
                rootPath,
                destinationPrefix,
                files,
                seenRelativePaths,
                warnings,
                progress,
                cancellationToken);
        }

        return new ModuleScanResult(
            Id,
            files,
            warnings,
            new Dictionary<string, string>
            {
                ["ComputerName"] = Environment.MachineName,
                ["UserName"] = Environment.UserName,
                ["Mode"] = request.Mode.ToString(),
                ["RootCount"] = roots.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    private static (string RootPath, string DestinationPrefix)[] BuildRoots(
        MigrationRequest request)
    {
        var roots = new List<(string, string)>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var category in CategoryNames.Keys.Where(request.Categories.Contains))
        {
            var path = category switch
            {
                MigrationCategory.Desktop =>
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                MigrationCategory.Documents =>
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                MigrationCategory.Downloads => Path.Combine(userProfile, "Downloads"),
                MigrationCategory.Pictures =>
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                MigrationCategory.Music =>
                    Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                MigrationCategory.Videos =>
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                _ => throw new ArgumentOutOfRangeException(nameof(request), category, null),
            };

            if (!string.IsNullOrWhiteSpace(path))
            {
                roots.Add((Path.GetFullPath(path), CategoryNames[category]));
            }
        }

        if (request.Mode == MigrationMode.Custom &&
            request.Categories.Contains(MigrationCategory.CustomFiles))
        {
            for (var index = 0; index < request.CustomPaths.Count; index++)
            {
                var path = request.CustomPaths[index];
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(path);
                var name = new DirectoryInfo(fullPath).Name;
                roots.Add((fullPath, Path.Combine("Custom", $"{index + 1:D2}-{name}")));
            }
        }

        return roots
            .DistinctBy(root => root.Item1, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void EnumerateRoot(
        string rootPath,
        string destinationPrefix,
        List<MigrationFile> files,
        HashSet<string> seenRelativePaths,
        List<string> warnings,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.TryPop(out var currentDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push(directory);
                        }
                    }
                    catch (Exception exception) when (
                        exception is UnauthorizedAccessException or IOException)
                    {
                        warnings.Add($"Accès impossible à « {directory} »: {exception.Message}");
                    }
                }

                foreach (var filePath in Directory.EnumerateFiles(currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        var relativePath = Path.Combine(
                            destinationPrefix,
                            Path.GetRelativePath(rootPath, filePath));

                        if (!seenRelativePaths.Add(relativePath))
                        {
                            warnings.Add(
                                $"Le chemin de destination « {relativePath} » était en double.");
                            continue;
                        }

                        files.Add(new MigrationFile(
                            Id,
                            fileInfo.FullName,
                            relativePath,
                            fileInfo.Length,
                            fileInfo.LastWriteTimeUtc));

                        if (files.Count % 100 == 0)
                        {
                            progress?.Report(new MigrationProgress(
                                "Scan",
                                $"{files.Count:N0} fichiers trouvés…",
                                0,
                                TotalBytes: files.Sum(file => file.Length),
                                CurrentItem: fileInfo.Name));
                        }
                    }
                    catch (Exception exception) when (
                        exception is UnauthorizedAccessException or IOException)
                    {
                        warnings.Add($"Fichier ignoré « {filePath} »: {exception.Message}");
                    }
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                warnings.Add(
                    $"Impossible d'analyser « {currentDirectory} »: {exception.Message}");
            }
        }
    }
}
