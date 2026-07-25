namespace SmartSwitch.Core.Models;

public sealed record SendTransferRequest(
    string Host,
    int Port,
    PairingCode PairingCode,
    IReadOnlyList<MigrationFile> Files);

public sealed record ReceiveTransferRequest(
    int Port,
    PairingCode PairingCode,
    string DestinationRoot,
    bool ListenOnLoopbackOnly = false);

public sealed record TransferResult(
    bool Succeeded,
    int FileCount,
    long TotalBytes,
    string PeerComputerName,
    string DestinationPath,
    IReadOnlyList<string> Warnings);
