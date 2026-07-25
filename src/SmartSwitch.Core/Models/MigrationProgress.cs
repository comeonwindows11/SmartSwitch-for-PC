namespace SmartSwitch.Core.Models;

public sealed record MigrationProgress(
    string Stage,
    string Message,
    double Percentage,
    long BytesProcessed = 0,
    long TotalBytes = 0,
    string? CurrentItem = null);
