using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Infrastructure.PreOs;

public sealed class PreOsService : IPreOsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IPrivilegeService _privilegeService;

    public PreOsService(IPrivilegeService privilegeService)
    {
        _privilegeService = privilegeService;
    }

    public Task<PreOsCapability> GetCapabilityAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new PreOsCapability(
                PreOsCapabilityState.UnsupportedPlatform,
                "Le redémarrage pré-OS est disponible uniquement sous Windows.",
                null,
                null,
                null));
        }

        if (!_privilegeService.IsAdministrator)
        {
            return Task.FromResult(new PreOsCapability(
                PreOsCapabilityState.AdministratorRequired,
                "Les privilèges administrateur sont requis pour préparer le démarrage pré-OS.",
                null,
                null,
                null));
        }

        var preOsRoot = Path.Combine(AppContext.BaseDirectory, "PreOS");
        var agentPath = Path.Combine(preOsRoot, "Agent", "SmartSwitch.PreOs.Agent.exe");
        var bootWimPath = Path.Combine(preOsRoot, "Media", "sources", "boot.wim");
        var bootSdiPath = Path.Combine(preOsRoot, "Media", "boot", "boot.sdi");
        if (!File.Exists(agentPath))
        {
            return Task.FromResult(new PreOsCapability(
                PreOsCapabilityState.AgentMissing,
                "L'agent pré-OS est absent de l'installation.",
                agentPath,
                bootWimPath,
                bootSdiPath));
        }

        if (!File.Exists(bootWimPath) || !File.Exists(bootSdiPath))
        {
            return Task.FromResult(new PreOsCapability(
                PreOsCapabilityState.MediaMissing,
                "L'image WinPE n'est pas provisionnée. Exécutez Build-WinPEImage.ps1 sur un poste avec le Windows ADK.",
                agentPath,
                bootWimPath,
                bootSdiPath));
        }

        return Task.FromResult(new PreOsCapability(
            PreOsCapabilityState.Ready,
            "L'agent et l'image de démarrage pré-OS sont disponibles.",
            agentPath,
            bootWimPath,
            bootSdiPath));
    }

    public async Task<PreOsJob> PrepareJobAsync(
        Guid sessionId,
        string packagePath,
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Identifiant de session invalide.", nameof(sessionId));
        }

        var capability = await GetCapabilityAsync(cancellationToken);
        if (!capability.IsReady)
        {
            throw new InvalidOperationException(capability.Message);
        }

        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullTargetRoot = Path.GetFullPath(targetRoot);
        if (!File.Exists(fullPackagePath) || !Directory.Exists(fullTargetRoot))
        {
            throw new InvalidOperationException(
                "Le package reçu ou le dossier utilisateur cible est introuvable.");
        }

        var job = new PreOsJob(
            Guid.NewGuid(),
            sessionId,
            fullPackagePath,
            fullTargetRoot,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SmartSwitch",
                "PreOsJobs",
                sessionId.ToString("N")),
            DateTimeOffset.UtcNow);
        Directory.CreateDirectory(job.JobDirectory);
        await WriteJobAsync(job, cancellationToken);
        return job;
    }

    public async Task ScheduleAndRestartAsync(
        PreOsJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var capability = await GetCapabilityAsync(cancellationToken);
        if (!capability.IsReady ||
            capability.BootWimPath is null ||
            capability.BootSdiPath is null)
        {
            throw new InvalidOperationException(capability.Message);
        }

        var stableRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SmartSwitch",
            "PreOS");
        Directory.CreateDirectory(stableRoot);
        var stableWimPath = Path.Combine(stableRoot, "boot.wim");
        var stableSdiPath = Path.Combine(stableRoot, "boot.sdi");
        File.Copy(capability.BootWimPath, stableWimPath, overwrite: true);
        File.Copy(capability.BootSdiPath, stableSdiPath, overwrite: true);
        var backupPath = Path.Combine(job.JobDirectory, "bcd-backup");
        await RunAsync("bcdedit.exe", $"/export \"{backupPath}\"", cancellationToken);

        try
        {
            var ramdiskId = ParseBcdIdentifier(await RunAsync(
                "bcdedit.exe",
                "/create /d \"SmartSwitch Pre-OS Ramdisk\" /application ramdiskoptions",
                cancellationToken));
            var bootId = ParseBcdIdentifier(await RunAsync(
                "bcdedit.exe",
                "/create /d \"SmartSwitch Pre-OS Migration\" /application osloader",
                cancellationToken));
            var drive = Path.GetPathRoot(stableRoot)?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ??
                throw new InvalidOperationException("Volume de démarrage introuvable.");
            var wimBcdPath = stableWimPath[drive.Length..].TrimStart('\\').Replace('/', '\\');
            var sdiBcdPath = stableSdiPath[drive.Length..].TrimStart('\\').Replace('/', '\\');

            await RunAsync(
                "bcdedit.exe",
                $"/set {ramdiskId} ramdisksdidevice partition={drive}",
                cancellationToken);
            await RunAsync(
                "bcdedit.exe",
                $"/set {ramdiskId} ramdisksdipath \\{sdiBcdPath}",
                cancellationToken);
            await RunAsync(
                "bcdedit.exe",
                $"/set {bootId} device ramdisk=[{drive}]\\{wimBcdPath},{ramdiskId}",
                cancellationToken);
            await RunAsync(
                "bcdedit.exe",
                $"/set {bootId} osdevice ramdisk=[{drive}]\\{wimBcdPath},{ramdiskId}",
                cancellationToken);
            await RunAsync(
                "bcdedit.exe",
                $"/set {bootId} path \\windows\\system32\\boot\\winload.efi",
                cancellationToken);
            await RunAsync("bcdedit.exe", $"/set {bootId} systemroot \\windows", cancellationToken);
            await RunAsync("bcdedit.exe", $"/set {bootId} winpe yes", cancellationToken);
            await RunAsync("bcdedit.exe", $"/set {bootId} detecthal yes", cancellationToken);
            await RunAsync("bcdedit.exe", $"/bootsequence {bootId}", cancellationToken);

            job = job with { BootEntryId = bootId };
            await WriteJobAsync(job, cancellationToken);
            await RunAsync(
                "shutdown.exe",
                "/r /t 15 /d p:4:1 /c \"SmartSwitch prépare la migration pré-OS.\"",
                cancellationToken);
        }
        catch
        {
            if (File.Exists(backupPath))
            {
                try
                {
                    await RunAsync("bcdedit.exe", $"/import \"{backupPath}\"", CancellationToken.None);
                }
                catch (Exception)
                {
                    // The BCD backup is retained for an administrator-led recovery.
                }
            }

            throw;
        }
    }

    private static async Task WriteJobAsync(PreOsJob job, CancellationToken cancellationToken)
    {
        var jobPath = Path.Combine(job.JobDirectory, "preos-job.json");
        var temporaryPath = jobPath + ".partial";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, job, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, jobPath, overwrite: true);
    }

    private static async Task<string> RunAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} a échoué ({process.ExitCode}) : {error}");
        }

        return output;
    }

    private static string ParseBcdIdentifier(string output)
    {
        var match = Regex.Match(output, @"\{[0-9a-fA-F-]{36}\}");
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "BCDEdit n'a pas retourné l'identifiant de l'entrée créée.");
        }

        return match.Value;
    }
}
