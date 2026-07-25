using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Abstractions;

public interface IMigrationLogger
{
    event EventHandler<MigrationLogEntry>? EntryWritten;

    Task LogAsync(
        MigrationLogLevel level,
        string source,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default);
}
