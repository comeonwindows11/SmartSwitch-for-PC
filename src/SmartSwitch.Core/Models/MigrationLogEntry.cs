namespace SmartSwitch.Core.Models;

public enum MigrationLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public sealed record MigrationLogEntry(
    DateTimeOffset TimestampUtc,
    MigrationLogLevel Level,
    string Source,
    string Message,
    string? Exception = null);
