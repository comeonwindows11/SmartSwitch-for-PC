using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Tests;

internal sealed class TestLogger : IMigrationLogger, IDisposable
{
    public event EventHandler<MigrationLogEntry>? EntryWritten;

    public List<MigrationLogEntry> Entries { get; } = [];

    public Task LogAsync(
        MigrationLogLevel level,
        string source,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new MigrationLogEntry(
            DateTimeOffset.UtcNow,
            level,
            source,
            message,
            exception?.ToString());
        Entries.Add(entry);
        EntryWritten?.Invoke(this, entry);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
