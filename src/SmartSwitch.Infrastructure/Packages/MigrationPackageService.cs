using System.Globalization;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Infrastructure.Packages;

public sealed class MigrationPackageService : IMigrationPackageService
{
    private const int BufferSize = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IMigrationLogger _logger;

    public MigrationPackageService(IMigrationLogger logger)
    {
        _logger = logger;
    }

    public async Task<PreparedMigrationPackage> BuildAsync(
        MigrationPackageBuildRequest request,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId == Guid.Empty)
        {
            throw new ArgumentException("Un identifiant de session est requis.", nameof(request));
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var packagePath = Path.Combine(outputDirectory, $"{request.SessionId:N}.smartswitch");
        var temporaryPath = packagePath + ".partial";
        var warnings = new List<string>();
        var files = new List<PackageFileEntry>();
        var blobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        TryDelete(temporaryPath);
        await _logger.LogAsync(
            MigrationLogLevel.Information,
            nameof(MigrationPackageService),
            $"Création du package {request.SessionId:N}.",
            cancellationToken: cancellationToken);

        try
        {
            await using (var packageStream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var totalSourceFiles = request.FileScan.Files.Count +
                    request.SelectedApplications.Count(
                        application => application.Transferability == ApplicationTransferability.PortableCopy);
                var processedFiles = 0;

                foreach (var migrationFile in request.FileScan.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(migrationFile.SourcePath))
                    {
                        warnings.Add($"Fichier absent pendant la préparation : {migrationFile.SourcePath}");
                        continue;
                    }

                    var descriptor = await AddBlobAsync(
                        archive,
                        migrationFile.SourcePath,
                        request.Compress,
                        blobs,
                        cancellationToken);
                    files.Add(new PackageFileEntry(
                        migrationFile.ModuleId,
                        PackageContentKind.UserFile,
                        NormalizeDestinationPath(migrationFile.RelativePath),
                        migrationFile.LastWriteTimeUtc,
                        descriptor));
                    ReportBuildProgress(
                        progress,
                        ++processedFiles,
                        Math.Max(1, totalSourceFiles),
                        migrationFile.RelativePath);
                }

                foreach (var application in request.SelectedApplications.Where(
                             candidate => candidate.Transferability == ApplicationTransferability.PortableCopy))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(application.InstallLocation) ||
                        !Directory.Exists(application.InstallLocation))
                    {
                        warnings.Add(
                            $"Application portable ignorée (dossier absent) : {application.DisplayName}");
                        continue;
                    }

                    var applicationRoot = application.InstallLocation;
                    foreach (var sourcePath in EnumerateFilesSafely(applicationRoot, warnings))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(applicationRoot, sourcePath);
                        var descriptor = await AddBlobAsync(
                            archive,
                            sourcePath,
                            request.Compress,
                            blobs,
                            cancellationToken);
                        files.Add(new PackageFileEntry(
                            "applications",
                            PackageContentKind.PortableApplicationFile,
                            Path.Combine("PortableApps", application.Id, relative),
                            File.GetLastWriteTimeUtc(sourcePath),
                            descriptor));
                        ReportBuildProgress(
                            progress,
                            ++processedFiles,
                            Math.Max(1, totalSourceFiles),
                            application.DisplayName);
                    }
                }

                var applications = request.SelectedApplications
                    .Select(application => new PackageApplicationEntry(
                        application.Id,
                        application.DisplayName,
                        application.Version,
                        application.Publisher,
                        application.Transferability,
                        application.TransferabilityReason,
                        application.Dependencies,
                        application.SilentInstallArguments))
                    .ToArray();
                var systemSettings = request.IncludeSystemSettings
                    ? CaptureSystemSettings()
                    : null;
                var networkSettings = request.IncludeNetworkSettings
                    ? CaptureNetworkSettings()
                    : null;

                if (systemSettings is not null)
                {
                    await AddJsonEntryAsync(
                        archive,
                        "settings/system.json",
                        systemSettings,
                        cancellationToken);
                }

                if (networkSettings is not null)
                {
                    await AddJsonEntryAsync(
                        archive,
                        "settings/network.json",
                        networkSettings,
                        cancellationToken);
                }

                if (applications.Length > 0)
                {
                    await AddJsonEntryAsync(
                        archive,
                        "applications/inventory.json",
                        applications,
                        cancellationToken);
                }

                var unsignedManifest = new MigrationPackageManifest(
                    MigrationPackageSchema.CurrentVersion,
                    request.SessionId,
                    DateTimeOffset.UtcNow,
                    Environment.MachineName,
                    Environment.OSVersion.VersionString,
                    System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                    Environment.OSVersion.Version.Build,
                    RequiresPreOs: true,
                    files,
                    applications,
                    systemSettings,
                    networkSettings,
                    files.Sum(file => file.Blob.Length),
                    Signature: null);
                var manifest = request.Sign
                    ? SignManifest(unsignedManifest)
                    : unsignedManifest;
                await AddJsonEntryAsync(
                    archive,
                    MigrationPackageSchema.ManifestEntryName,
                    manifest,
                    cancellationToken);
            }

            File.Move(temporaryPath, packagePath, overwrite: false);
            var packageFile = new FileInfo(packagePath);
            var packageHash = await ComputeSha256Async(packagePath, cancellationToken);
            var validation = await ValidateAsync(packagePath, progress, cancellationToken);
            if (!validation.IsValid || validation.Manifest is null)
            {
                throw new InvalidDataException(
                    $"Le package créé n'est pas valide : {string.Join(" ", validation.Errors)}");
            }

            await _logger.LogAsync(
                MigrationLogLevel.Information,
                nameof(MigrationPackageService),
                $"Package créé : {packageFile.Length:N0} octets, {validation.Manifest.Files.Count} éléments.",
                cancellationToken: cancellationToken);
            return new PreparedMigrationPackage(
                request.SessionId,
                packagePath,
                packageFile.Length,
                packageHash,
                validation.Manifest,
                warnings.Concat(validation.Warnings).ToArray());
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public async Task<PackageValidationResult> ValidateAsync(
        string packagePath,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        MigrationPackageManifest? manifest = null;
        try
        {
            await using var packageStream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
            var manifestEntry = archive.GetEntry(MigrationPackageSchema.ManifestEntryName);
            if (manifestEntry is null)
            {
                return new PackageValidationResult(
                    false,
                    null,
                    ["Le manifeste du package est introuvable."],
                    warnings);
            }

            await using (var stream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<MigrationPackageManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken);
            }

            if (manifest is null)
            {
                return new PackageValidationResult(
                    false,
                    null,
                    ["Le manifeste du package est illisible."],
                    warnings);
            }

            ValidateManifest(manifest, errors);
            VerifyManifestSignature(manifest, errors);
            var descriptors = manifest.Files
                .Select(file => file.Blob)
                .GroupBy(blob => blob.BlobPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            for (var index = 0; index < descriptors.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = descriptors[index];
                var entry = archive.GetEntry(descriptor.BlobPath);
                if (entry is null)
                {
                    errors.Add($"Blob absent : {descriptor.BlobPath}");
                    continue;
                }

                await using var entryStream = entry.Open();
                var actualHash = await ComputeSha256Async(entryStream, cancellationToken);
                if (!string.Equals(actualHash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Empreinte invalide : {descriptor.BlobPath}");
                }

                if (entry.Length != descriptor.Length)
                {
                    errors.Add($"Taille invalide : {descriptor.BlobPath}");
                }

                progress?.Report(new MigrationProgress(
                    "Validation",
                    $"Vérification du package ({index + 1}/{descriptors.Length})…",
                    descriptors.Length == 0 ? 100 : (index + 1) * 100d / descriptors.Length,
                    CurrentItem: descriptor.BlobPath));
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException or CryptographicException)
        {
            errors.Add(exception.Message);
        }

        return new PackageValidationResult(errors.Count == 0, manifest, errors, warnings);
    }

    public async Task<MigrationPackageManifest> ReadManifestAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateAsync(packagePath, cancellationToken: cancellationToken);
        if (!validation.IsValid || validation.Manifest is null)
        {
            throw new InvalidDataException(
                $"Package invalide : {string.Join(" ", validation.Errors)}");
        }

        return validation.Manifest;
    }

    private static async Task<PackageBlobDescriptor> AddBlobAsync(
        ZipArchive archive,
        string sourcePath,
        bool compress,
        ISet<string> existingBlobHashes,
        CancellationToken cancellationToken)
    {
        var hash = await ComputeSha256Async(sourcePath, cancellationToken);
        var length = new FileInfo(sourcePath).Length;
        var blobPath = Path.Combine("blobs", hash[..2], hash).Replace('\', '/');
        if (existingBlobHashes.Add(hash))
        {
            var entry = archive.CreateEntry(
                blobPath,
                compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
            await using var output = entry.Open();
            await using var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, BufferSize, cancellationToken);
        }

        return new PackageBlobDescriptor(blobPath, hash, length);
    }

    private static async Task AddJsonEntryAsync<T>(
        ZipArchive archive,
        string entryPath,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static MigrationPackageManifest SignManifest(MigrationPackageManifest manifest)
    {
        using var algorithm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var signature = algorithm.SignData(payload, HashAlgorithmName.SHA256);
        return manifest with
        {
            Signature = new PackageSignature(
                "ECDSA_P256_SHA256",
                Convert.ToBase64String(algorithm.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(signature)),
        };
    }

    private static void VerifyManifestSignature(
        MigrationPackageManifest manifest,
        ICollection<string> errors)
    {
        if (manifest.Signature is null)
        {
            errors.Add("Le package n'est pas signé.");
            return;
        }

        if (!string.Equals(
                manifest.Signature.Algorithm,
                "ECDSA_P256_SHA256",
                StringComparison.Ordinal))
        {
            errors.Add("L'algorithme de signature du package est inconnu.");
            return;
        }

        try
        {
            using var algorithm = ECDsa.Create();
            algorithm.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(manifest.Signature.PublicKey),
                out _);
            var unsigned = manifest with { Signature = null };
            var payload = JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions);
            if (!algorithm.VerifyData(
                    payload,
                    Convert.FromBase64String(manifest.Signature.Value),
                    HashAlgorithmName.SHA256))
            {
                errors.Add("La signature du manifeste est invalide.");
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            errors.Add($"Signature de package invalide : {exception.Message}");
        }
    }

    private static void ValidateManifest(
        MigrationPackageManifest manifest,
        ICollection<string> errors)
    {
        if (manifest.SchemaVersion != MigrationPackageSchema.CurrentVersion)
        {
            errors.Add("La version du manifeste n'est pas prise en charge.");
        }

        if (manifest.SessionId == Guid.Empty)
        {
            errors.Add("L'identifiant de session est absent.");
        }

        if (manifest.TotalUncompressedBytes < 0)
        {
            errors.Add("La taille totale du manifeste est invalide.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!IsSafeRelativePath(file.DestinationPath) ||
                !IsSafeRelativePath(file.Blob.BlobPath))
            {
                errors.Add($"Chemin de package invalide : {file.DestinationPath}");
            }

            if (!paths.Add(file.DestinationPath))
            {
                errors.Add($"Chemin de destination présent plusieurs fois : {file.DestinationPath}");
            }

            if (file.Blob.Length < 0 || file.Blob.Sha256.Length != 64)
            {
                errors.Add($"Descripteur de blob invalide : {file.DestinationPath}");
            }
        }

        if (manifest.TotalUncompressedBytes != manifest.Files.Sum(file => file.Blob.Length))
        {
            errors.Add("La taille totale ne correspond pas aux fichiers annoncés.");
        }
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Contains(':', StringComparison.Ordinal) &&
        !path.Split(['/', '\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");

    private static string NormalizeDestinationPath(string path)
    {
        if (!IsSafeRelativePath(path))
        {
            throw new InvalidDataException($"Chemin de destination invalide : {path}");
        }

        return path.Replace('\', '/');
    }

    private static IEnumerable<string> EnumerateFilesSafely(
        string root,
        ICollection<string> warnings)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push(directory);
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"Dossier portable ignoré : {exception.Message}");
                    }
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    yield return file;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Application portable incomplète : {exception.Message}");
            }
        }
    }

    private static SystemSettingsSnapshot CaptureSystemSettings()
    {
        using var explorer = Registry.CurrentUser.OpenSubKey(
            @"SoftwareMicrosoftWindowsCurrentVersionExplorerAdvanced");
        using var personalize = Registry.CurrentUser.OpenSubKey(
            @"SoftwareMicrosoftWindowsCurrentVersionThemesPersonalize");
        return new SystemSettingsSnapshot(
            CultureInfo.CurrentCulture.Name,
            CultureInfo.CurrentUICulture.Name,
            TimeZoneInfo.Local.Id,
            ReadNullableBool(personalize, "AppsUseLightTheme"),
            ReadNullableBool(explorer, "Hidden"),
            ReadNullableBool(explorer, "HideFileExt") is { } hide ? !hide : null);
    }

    private static NetworkSettingsSnapshot CaptureNetworkSettings()
    {
        var adapters = new List<NetworkAdapterSnapshot>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = adapter.GetIPProperties();
                var ipv4 = properties.GetIPv4Properties();
                adapters.Add(new NetworkAdapterSnapshot(
                    adapter.Name,
                    adapter.Description,
                    adapter.NetworkInterfaceType.ToString(),
                    ipv4.IsDhcpEnabled,
                    properties.UnicastAddresses
                        .Where(address => address.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                        .Select(address => address.Address.ToString())
                        .ToArray(),
                    properties.GatewayAddresses
                        .Select(address => address.Address.ToString())
                        .ToArray(),
                    properties.DnsAddresses
                        .Select(address => address.ToString())
                        .ToArray()));
            }
            catch (NetworkInformationException)
            {
                // The adapter metadata is optional and must never block package creation.
            }
        }

        return new NetworkSettingsSnapshot(adapters);
    }

    private static bool? ReadNullableBool(RegistryKey? key, string valueName) =>
        key?.GetValue(valueName) switch
        {
            int value => value != 0,
            _ => null,
        };

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeSha256Async(stream, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ReportBuildProgress(
        IProgress<MigrationProgress>? progress,
        int processed,
        int total,
        string currentItem) =>
        progress?.Report(new MigrationProgress(
            "Package",
            $"Préparation du package ({processed:N0}/{total:N0})…",
            processed * 100d / total,
            CurrentItem: currentItem));

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
