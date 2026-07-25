using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Services;

public sealed class MigrationEngine : IMigrationEngine
{
    private readonly IReadOnlyDictionary<string, IMigrationModule> _modulesById;
    private readonly IMigrationLogger _logger;

    public MigrationEngine(IEnumerable<IMigrationModule> modules, IMigrationLogger logger)
    {
        _logger = logger;
        var materialized = modules.ToArray();
        var duplicate = materialized
            .GroupBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Plusieurs modules utilisent l'identifiant '{duplicate.Key}'.");
        }

        _modulesById = materialized.ToDictionary(
            module => module.Id,
            StringComparer.OrdinalIgnoreCase);
        Modules = materialized;
    }

    public IReadOnlyCollection<IMigrationModule> Modules { get; }

    public async Task<MigrationScanSummary> ScanAsync(
        MigrationRequest request,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = DateTimeOffset.UtcNow;
        var selectedModules = ResolveExecutionOrder(request);
        var results = new List<ModuleScanResult>(selectedModules.Count);

        await _logger.LogAsync(
            MigrationLogLevel.Information,
            nameof(MigrationEngine),
            $"Scan {request.Mode} démarré avec {selectedModules.Count} module(s).",
            cancellationToken: cancellationToken);

        for (var index = 0; index < selectedModules.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var module = selectedModules[index];
            progress?.Report(new MigrationProgress(
                "Scan",
                $"Analyse avec {module.DisplayName}",
                selectedModules.Count == 0 ? 100 : index * 100d / selectedModules.Count));

            try
            {
                results.Add(await module.ScanAsync(request, progress, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await _logger.LogAsync(
                    MigrationLogLevel.Error,
                    module.Id,
                    "Le scan du module a échoué.",
                    exception,
                    cancellationToken);
                throw new MigrationModuleException(module.Id, exception);
            }
        }

        var summary = new MigrationScanSummary(results, startedAt, DateTimeOffset.UtcNow);
        progress?.Report(new MigrationProgress(
            "Scan",
            $"Scan terminé: {summary.Files.Count} fichier(s).",
            100,
            TotalBytes: summary.TotalBytes));

        await _logger.LogAsync(
            MigrationLogLevel.Information,
            nameof(MigrationEngine),
            $"Scan terminé: {summary.Files.Count} fichier(s), {summary.TotalBytes} octets.",
            cancellationToken: cancellationToken);

        return summary;
    }

    private List<IMigrationModule> ResolveExecutionOrder(MigrationRequest request)
    {
        var enabled = request.EnabledModuleIds is { Count: > 0 }
            ? request.EnabledModuleIds
            : _modulesById.Values
                .Where(module => module.SupportedCategories.Any(request.Categories.Contains))
                .Select(module => module.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ordered = new List<IMigrationModule>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var moduleId in enabled.Order(StringComparer.OrdinalIgnoreCase))
        {
            Visit(moduleId);
        }

        return ordered;

        void Visit(string moduleId)
        {
            if (visited.Contains(moduleId))
            {
                return;
            }

            if (!_modulesById.TryGetValue(moduleId, out var module))
            {
                throw new InvalidOperationException($"Le module '{moduleId}' est introuvable.");
            }

            if (!visiting.Add(moduleId))
            {
                throw new InvalidOperationException(
                    $"Une dépendance circulaire implique le module '{moduleId}'.");
            }

            foreach (var dependency in module.Dependencies)
            {
                Visit(dependency);
            }

            visiting.Remove(moduleId);
            visited.Add(moduleId);
            ordered.Add(module);
        }
    }
}

public sealed class MigrationModuleException : Exception
{
    public MigrationModuleException(string moduleId, Exception innerException)
        : base($"Le module de migration '{moduleId}' a échoué.", innerException)
    {
        ModuleId = moduleId;
    }

    public string ModuleId { get; }
}
