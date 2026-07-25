namespace SmartSwitch.Core.Models;

public sealed record MigrationFile(
    string ModuleId,
    string SourcePath,
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc);
