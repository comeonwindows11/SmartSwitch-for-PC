using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Infrastructure.PreOs;

public sealed class PreOsPackageApplier : IPreOsPackageApplier
{
    private const int BufferSize = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IMigrationPackageService _packageService;

    public PreOsPackageApplier(IMigrationPackageService packageService)
    {
        _packageService = packageService;
    }

    public async Task<PreOsApplyResult> ApplyAsync(
        PreOsJob job,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var targetRoot = Path.GetFullPath(job.TargetRoot);
        if (!Directory.Exists(targetRoot))
        {
            throw new DirectoryNotFoundException(
                $"Le volume cible est introuvable : {targetRoot}");
        }

        var validation = await _packageService.ValidateAsync(
            job.PackagePath,
            progress,
            cancellationToken);
        if (!validation.IsValid || validation.Manifest is null)
        {
            throw new InvalidDataException(
                $"Package refusé : {string.Join(" ", validation.Errors)}");
        }

        var manifest = validation.Manifest;
        if (manifest.SessionId != job.SessionId)
        {
            throw new InvalidDataException(
                "Le package ne correspond pas à la tâche pré-OS préparée.");
        }

        Directory.CreateDirectory(job.JobDirectory);
        var journalPath = Path.Combine(job.JobDirectory, "apply-journal.json");
        var logPath = Path.Combine(job.JobDirectory, "apply.jsonl");
        var stageRoot = Path.Combine(targetRoot, ".smartswitch-stage", job.JobId.ToString("N"));
        var backupRoot = Path.Combine(targetRoot, ".smartswitch-backup", job.JobId.ToString("N"));
        Directory.CreateDirectory(stageRoot);
        Directory.CreateDirectory(backupRoot);

        var entries = new List<PreOsApplyJournalEntry>();
        var journal = new PreOsApplyJournal(
            job.JobId,
            job.SessionId,
            DateTimeOffset.UtcNow,
            Completed: false,
            entries);
        await WriteJournalAsync(journalPath, journal, cancellationToken);
        await WriteLogAsync(logPath, "Preflight", "Package validé; application démarrée.", cancellationToken);

        try
        {
            await using var packageStream = new FileStream(
                job.PackagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
            for (var index = 0; index < manifest.Files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageFile = manifest.Files[index];
                var destinationPath = GetSafeTargetPath(targetRoot, packageFile.DestinationPath);
                EnsureNoReparsePointInExistingParents(targetRoot, destinationPath);
                var stagePath = Path.Combine(stageRoot, $"{index:D8}.blob");
                var backupPath = Path.Combine(backupRoot, $"{index:D8}.backup");
                var journalEntry = new PreOsApplyJournalEntry(
                    destinationPath,
                    packageFile.Blob.Sha256,
                    PreOsApplyState.Pending,
                    BackupPath: null,
                    Error: null);
                entries.Add(journalEntry);
                journal = journal with { UpdatedAtUtc = DateTimeOffset.UtcNow, Entries = entries.ToArray() };
                await WriteJournalAsync(journalPath, journal, cancellationToken);

                var blobEntry = archive.GetEntry(packageFile.Blob.BlobPath) ??
                    throw new InvalidDataException(
                        $"Blob absent pendant l'application : {packageFile.Blob.BlobPath}");
                await ExtractBlobAsync(blobEntry, stagePath, packageFile.Blob, cancellationToken);
                journalEntry = journalEntry with { State = PreOsApplyState.Staged };
                entries[^1] = journalEntry;
                journal = journal with { UpdatedAtUtc = DateTimeOffset.UtcNow, Entries = entries.ToArray() };
                await WriteJournalAsync(journalPath, journal, cancellationToken);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? targetRoot);
                if (File.Exists(destinationPath))
                {
                    File.Move(destinationPath, backupPath, overwrite: false);
                    journalEntry = journalEntry with
                    {
                        State = PreOsApplyState.BackedUp,
                        BackupPath = backupPath,
                    };
                    entries[^1] = journalEntry;
                    journal = journal with { UpdatedAtUtc = DateTimeOffset.UtcNow, Entries = entries.ToArray() };
                    await WriteJournalAsync(journalPath, journal, cancellationToken);
                }

                File.Move(stagePath, destinationPath, overwrite: false);
                File.SetLastWriteTimeUtc(destinationPath, packageFile.LastWriteTimeUtc.UtcDateTime);
                journalEntry = journalEntry with { State = PreOsApplyState.Replaced };
                entries[^1] = journalEntry;
                journal = journal with { UpdatedAtUtc = DateTimeOffset.UtcNow, Entries = entries.ToArray() };
                await WriteJournalAsync(journalPath, journal, cancellationToken);

                if (!string.Equals(
                        await ComputeSha256Async(destinationPath, cancellationToken),
                        packageFile.Blob.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Intégrité invalide après application : {packageFile.DestinationPath}");
                }

                journalEntry = journalEntry with { State = PreOsApplyState.Verified };
                entries[^1] = journalEntry;
                journal = journal with { UpdatedAtUtc = DateTimeOffset.UtcNow, Entries = entries.ToArray() };
                await WriteJournalAsync(journalPath, journal, cancellationToken);
                await WriteLogAsync(
                    logPath,
                    "Apply",
                    $"Appliqué : {packageFile.DestinationPath}",
                    cancellationToken);
                progress?.Report(new MigrationProgress(
                    "Pré-OS",
                    $"Application des données ({index + 1}/{manifest.Files.Count})…",
                    manifest.Files.Count == 0 ? 100 : (index + 1) * 100d / manifest.Files.Count,
                    CurrentItem: packageFile.DestinationPath));
            }

            for (var index = 0; index < entries.Count; index++)
            {
                entries[index] = entries[index] with { State = PreOsApplyState.Committed };
            }

            journal = journal with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Completed = true,
                Entries = entries.ToArray(),
            };
            await WriteJournalAsync(journalPath, journal, cancellationToken);
            await WriteLogAsync(logPath, "Complete", "Application terminée.", cancellationToken);
            var deferred = manifest.Applications.Count(
                application => application.Transferability == ApplicationTransferability.ReinstallPlan);
            var warnings = new List<string>();
            if (deferred > 0)
            {
                warnings.Add(
                    $"{deferred:N0} application(s) nécessitent une réinstallation après le redémarrage de Windows.");
            }

            if (manifest.SystemSettings is not null || manifest.NetworkSettings is not null)
            {
                warnings.Add(
                    "Les paramètres système et réseau sont conservés dans le package pour une application validée après Windows.");
            }

            return new PreOsApplyResult(
                true,
                manifest.Files.Count,
                deferred,
                journalPath,
                logPath,
                warnings);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteLogAsync(logPath, "Rollback", exception.Message, CancellationToken.None);
            await RollbackAsync(entries, journalPath, job, CancellationToken.None);
            throw;
        }
    }

    private static async Task RollbackAsync(
        IList<PreOsApplyJournalEntry> entries,
        string journalPath,
        PreOsJob job,
        CancellationToken cancellationToken)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            try
            {
                if (entry.State is PreOsApplyState.Replaced or PreOsApplyState.Verified)
                {
                    if (File.Exists(entry.DestinationPath))
                    {
                        File.Delete(entry.DestinationPath);
                    }

                    if (!string.IsNullOrWhiteSpace(entry.BackupPath) && File.Exists(entry.BackupPath))
                    {
                        File.Move(entry.BackupPath, entry.DestinationPath, overwrite: false);
                    }
                }

                entries[index] = entry with { State = PreOsApplyState.RolledBack };
            }
            catch (Exception rollbackException) when (
                rollbackException is IOException or UnauthorizedAccessException)
            {
                entries[index] = entry with
                {
                    State = PreOsApplyState.Failed,
                    Error = rollbackException.Message,
                };
            }
        }

        var journal = new PreOsApplyJournal(
            job.JobId,
            job.SessionId,
            DateTimeOffset.UtcNow,
            Completed: false,
            entries.ToArray());
        await WriteJournalAsync(journalPath, journal, cancellationToken);
    }

    private static async Task ExtractBlobAsync(
        ZipArchiveEntry blobEntry,
        string stagePath,
        PackageBlobDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        await using var input = blobEntry.Open();
        await using var output = new FileStream(
            stagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, BufferSize, cancellationToken);
        await output.FlushAsync(cancellationToken);
        var info = new FileInfo(stagePath);
        if (info.Length != descriptor.Length ||
            !string.Equals(
                await ComputeSha256Async(stagePath, cancellationToken),
                descriptor.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Blob corrompu : {descriptor.BlobPath}");
        }
    }

    private static string GetSafeTargetPath(string targetRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("Le package contient un chemin de destination interdit.");
        }

        var firstSegment = relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (firstSegment is null ||
            !new[]
            {
                "Desktop",
                "Documents",
                "Downloads",
                "Pictures",
                "Music",
                "Videos",
                "Custom",
                "PortableApps",
            }.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Le package cible un emplacement pré-OS interdit.");
        }

        var normalizedRoot = Path.GetFullPath(targetRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!destination.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Une sortie du volume cible a été bloquée.");
        }

        return destination;
    }

    private static void EnsureNoReparsePointInExistingParents(string root, string destination)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(destination) ?? root);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (current.Exists &&
               current.FullName.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Point de jonction interdit dans la destination : {current.FullName}");
            }

            current = current.Parent;
        }
    }

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
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static async Task WriteJournalAsync(
        string journalPath,
        PreOsApplyJournal journal,
        CancellationToken cancellationToken)
    {
        var temporaryPath = journalPath + ".partial";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, journal, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, journalPath, overwrite: true);
    }

    private static Task WriteLogAsync(
        string logPath,
        string phase,
        string message,
        CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Phase = phase,
            Message = message,
        }) + Environment.NewLine;
        return File.AppendAllTextAsync(logPath, line, cancellationToken);
    }
}
