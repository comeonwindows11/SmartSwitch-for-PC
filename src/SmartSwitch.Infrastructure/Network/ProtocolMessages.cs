namespace SmartSwitch.Infrastructure.Network;

internal sealed record ClientHello(
    string Product,
    int ProtocolVersion,
    string ComputerName,
    string ClientNonce);

internal sealed record PairingChallenge(
    string Salt,
    string Challenge,
    string CertificateSha256);

internal sealed record PairingProof(string Proof);

internal sealed record AuthenticationResult(bool Accepted, string? Proof, string? Message);

internal sealed record TransferManifest(string ComputerName, int FileCount, long TotalBytes);

internal sealed record ProtocolAcknowledgement(
    bool Accepted,
    string? Message = null,
    string? DestinationPath = null);

internal sealed record FileHeader(string RelativePath, long Length, DateTimeOffset LastWriteTimeUtc);

internal sealed record FileTrailer(string Sha256);

internal sealed record TransferCompletion(int FileCount, long TotalBytes);
