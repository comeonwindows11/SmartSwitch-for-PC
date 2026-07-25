namespace SmartSwitch.Core.Models;

public sealed record MigrationScanSummary(
    IReadOnlyList<ModuleScanResult> Modules,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc)
{
    public IReadOnlyList<MigrationFile> Files =>
        Modules.SelectMany(module => module.Files).ToArray();

    public IReadOnlyList<string> Warnings =>
        Modules.SelectMany(module => module.Warnings).ToArray();

    public long TotalBytes => Modules.Sum(module => module.TotalBytes);
}
