using System.Text.Json;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Infrastructure.Logging;

public sealed class JsonFileMigrationLogger : IMigrationLogger, IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _logPath;
    private bool _disposed;

    public JsonFileMigrationLogger()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartSwitch",
            "Logs");
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"smartswitch-{DateTime.UtcNow:yyyyMMdd}.jsonl");
    }

    public event EventHandler<MigrationLogEntry>? EntryWritten;

    public async Task LogAsync(
        MigrationLogLevel level,
        string source,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = new MigrationLogEntry(
            DateTimeOffset.UtcNow,
            level,
            source,
            message,
            exception?.ToString());
        var json = JsonSerializer.Serialize(entry);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(
                _logPath,
                json + Environment.NewLine,
                cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        EntryWritten?.Invoke(this, entry);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
    }
}
