namespace SmartSwitch.Core.Models;

public sealed record SendTransferRequest(
    string Host,
    int Port,
    PairingCode PairingCode,
    IReadOnlyList<MigrationFile> Files,
    Guid? SessionId = null,
    TransferPreflightMetadata? Preflight = null);

public sealed record ReceiveTransferRequest(
    int Port,
    PairingCode PairingCode,
    string DestinationRoot,
    bool ListenOnLoopbackOnly = false,
    DateTimeOffset? PairingExpiresAtUtc = null,
    bool RequirePreOs = false);

public sealed record TransferResult(
    bool Succeeded,
    int FileCount,
    long TotalBytes,
    string PeerComputerName,
    string DestinationPath,
    IReadOnlyList<string> Warnings,
    Guid SessionId = default,
    TransferPreflightMetadata? Preflight = null,
    long ResumedBytes = 0);
