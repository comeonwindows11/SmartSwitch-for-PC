namespace SmartSwitch.Core.Models;

public sealed record ModuleScanResult(
    string ModuleId,
    IReadOnlyList<MigrationFile> Files,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, string> Metadata)
{
    public long TotalBytes => Files.Sum(file => file.Length);
}
