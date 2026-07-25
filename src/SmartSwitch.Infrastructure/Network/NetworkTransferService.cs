using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Infrastructure.Network;

public sealed class NetworkTransferService : INetworkTransferService
{
    public const int DefaultPort = 49_736;
    private const string ProductIdentifier = "SmartSwitch.Migration";
    private const int ProtocolVersion = 1;
    private const int BufferSize = 128 * 1024;
    private const int MaximumFileCount = 1_000_000;
    private readonly IMigrationLogger _logger;

    public NetworkTransferService(IMigrationLogger logger)
    {
        _logger = logger;
    }

    public async Task<TransferResult> SendAsync(
        SendTransferRequest request,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePort(request.Port);
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            throw new ArgumentException("L'adresse du PC receveur est requise.", nameof(request));
        }

        if (request.Files.Count == 0)
        {
            throw new InvalidOperationException("Aucun fichier n'a été sélectionné.");
        }

        await _logger.LogAsync(
            MigrationLogLevel.Information,
            nameof(NetworkTransferService),
            $"Connexion au receveur {request.Host}:{request.Port}.",
            cancellationToken: cancellationToken);

        using var client = new TcpClient();
        await client.ConnectAsync(request.Host, request.Port, cancellationToken);
#pragma warning disable CA5359 // The pairing proof below authenticates and binds this exact certificate fingerprint.
        await using var secureStream = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            static (_, _, _, _) => true);
#pragma warning restore CA5359

        await secureStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = ProductIdentifier,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ApplicationProtocols = [new SslApplicationProtocol("smartswitch/1")],
            },
            cancellationToken);

        var remoteCertificate = secureStream.RemoteCertificate ??
            throw new AuthenticationException("Le receveur n'a fourni aucun certificat TLS.");
        var certificateFingerprint = SHA256.HashData(remoteCertificate.GetRawCertData());
        await AuthenticateClientAsync(
            secureStream,
            request.PairingCode,
            certificateFingerprint,
            cancellationToken);

        var totalBytes = request.Files.Sum(file => file.Length);
        await ProtocolFrame.WriteAsync(
            secureStream,
            new TransferManifest(Environment.MachineName, request.Files.Count, totalBytes),
            cancellationToken);
        var manifestAcknowledgement =
            await ProtocolFrame.ReadAsync<ProtocolAcknowledgement>(
                secureStream,
                cancellationToken);
        EnsureAccepted(manifestAcknowledgement);

        var processedBytes = 0L;
        var processedFiles = 0;
        foreach (var file in request.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(file.SourcePath))
            {
                throw new FileNotFoundException(
                    "Un fichier a disparu depuis le scan.",
                    file.SourcePath);
            }

            await ProtocolFrame.WriteAsync(
                secureStream,
                new FileHeader(file.RelativePath, file.Length, file.LastWriteTimeUtc),
                cancellationToken);

            var hash = await SendFileAsync(
                secureStream,
                file,
                processedBytes,
                totalBytes,
                progress,
                cancellationToken);
            await ProtocolFrame.WriteAsync(
                secureStream,
                new FileTrailer(Convert.ToHexString(hash)),
                cancellationToken);

            var acknowledgement = await ProtocolFrame.ReadAsync<ProtocolAcknowledgement>(
                secureStream,
                cancellationToken);
            EnsureAccepted(acknowledgement);
            processedBytes += file.Length;
            processedFiles++;
        }

        await ProtocolFrame.WriteAsync(
            secureStream,
            new TransferCompletion(processedFiles, processedBytes),
            cancellationToken);
        var completion = await ProtocolFrame.ReadAsync<ProtocolAcknowledgement>(
            secureStream,
            cancellationToken);
        EnsureAccepted(completion);

        progress?.Report(new MigrationProgress(
            "Terminé",
            $"{processedFiles} fichier(s) transféré(s).",
            100,
            processedBytes,
            totalBytes));
        await _logger.LogAsync(
            MigrationLogLevel.Information,
            nameof(NetworkTransferService),
            $"Transfert terminé: {processedFiles} fichier(s), {processedBytes} octets.",
            cancellationToken: cancellationToken);

        return new TransferResult(
            true,
            processedFiles,
            processedBytes,
            request.Host,
            completion.DestinationPath ?? string.Empty,
            []);
    }

    public async Task<TransferResult> ReceiveAsync(
        ReceiveTransferRequest request,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePort(request.Port);
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);
        Directory.CreateDirectory(destinationRoot);

        var listener = new TcpListener(
            request.ListenOnLoopbackOnly ? IPAddress.Loopback : IPAddress.Any,
            request.Port);
        listener.Start(1);
        using var cancellationRegistration =
            cancellationToken.Register(static state => ((TcpListener)state!).Stop(), listener);

        await _logger.LogAsync(
            MigrationLogLevel.Information,
            nameof(NetworkTransferService),
            $"En attente d'un donneur sur le port {request.Port}.",
            cancellationToken: cancellationToken);
        progress?.Report(new MigrationProgress(
            "Association",
            "En attente du PC donneur…",
            0));

        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var secureStream = new SslStream(client.GetStream(), false);
            using var certificate = CreateEphemeralCertificate();
            await secureStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ApplicationProtocols = [new SslApplicationProtocol("smartswitch/1")],
                },
                cancellationToken);

            var fingerprint = SHA256.HashData(certificate.GetRawCertData());
            var peerName = await AuthenticateServerAsync(
                secureStream,
                request.PairingCode,
                fingerprint,
                cancellationToken);
            var manifest = await ProtocolFrame.ReadAsync<TransferManifest>(
                secureStream,
                cancellationToken);
            ValidateManifest(manifest);

            var sessionDirectory = CreateSessionDirectory(destinationRoot, manifest.ComputerName);
            await ProtocolFrame.WriteAsync(
                secureStream,
                new ProtocolAcknowledgement(true),
                cancellationToken);

            var receivedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var receivedBytes = 0L;
            for (var index = 0; index < manifest.FileCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var header = await ProtocolFrame.ReadAsync<FileHeader>(
                    secureStream,
                    cancellationToken);
                ValidateFileHeader(header, manifest.TotalBytes);
                var destinationPath = GetSafeDestinationPath(
                    sessionDirectory,
                    header.RelativePath);
                if (!receivedPaths.Add(destinationPath))
                {
                    throw new InvalidDataException(
                        $"Le chemin « {header.RelativePath} » est présent plusieurs fois.");
                }

                Directory.CreateDirectory(
                    Path.GetDirectoryName(destinationPath) ?? sessionDirectory);
                var temporaryPath = destinationPath + ".smartswitch-partial";
                byte[] actualHash;
                try
                {
                    actualHash = await ReceiveFileAsync(
                        secureStream,
                        temporaryPath,
                        header,
                        receivedBytes,
                        manifest.TotalBytes,
                        progress,
                        cancellationToken);
                    var trailer = await ProtocolFrame.ReadAsync<FileTrailer>(
                        secureStream,
                        cancellationToken);
                    var expectedHash = Convert.FromHexString(trailer.Sha256);
                    if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                    {
                        throw new InvalidDataException(
                            $"Le contrôle d'intégrité a échoué pour « {header.RelativePath} ».");
                    }

                    File.Move(temporaryPath, destinationPath, overwrite: false);
                    File.SetLastWriteTimeUtc(destinationPath, header.LastWriteTimeUtc.UtcDateTime);
                }
                catch
                {
                    TryDeletePartialFile(temporaryPath);
                    throw;
                }

                receivedBytes += header.Length;
                await ProtocolFrame.WriteAsync(
                    secureStream,
                    new ProtocolAcknowledgement(true),
                    cancellationToken);
            }

            var completion = await ProtocolFrame.ReadAsync<TransferCompletion>(
                secureStream,
                cancellationToken);
            if (completion.FileCount != manifest.FileCount ||
                completion.TotalBytes != receivedBytes)
            {
                throw new InvalidDataException(
                    "Le bilan final ne correspond pas au manifeste annoncé.");
            }

            await ProtocolFrame.WriteAsync(
                secureStream,
                new ProtocolAcknowledgement(
                    true,
                    DestinationPath: sessionDirectory),
                cancellationToken);
            progress?.Report(new MigrationProgress(
                "Terminé",
                $"{manifest.FileCount} fichier(s) reçu(s).",
                100,
                receivedBytes,
                manifest.TotalBytes));
            await _logger.LogAsync(
                MigrationLogLevel.Information,
                nameof(NetworkTransferService),
                $"Réception terminée depuis {peerName}: {receivedBytes} octets.",
                cancellationToken: cancellationToken);

            return new TransferResult(
                true,
                manifest.FileCount,
                receivedBytes,
                peerName,
                sessionDirectory,
                []);
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<byte[]> SendFileAsync(
        Stream destination,
        MigrationFile file,
        long alreadyProcessed,
        long totalBytes,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            file.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != file.Length)
        {
            throw new IOException($"La taille de « {file.SourcePath} » a changé depuis le scan.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        var fileBytes = 0L;
        while (fileBytes < file.Length)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Fin inattendue du fichier « {file.SourcePath} ».");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            fileBytes += read;
            ReportTransferProgress(
                progress,
                "Envoi",
                file.RelativePath,
                alreadyProcessed + fileBytes,
                totalBytes);
        }

        await destination.FlushAsync(cancellationToken);
        return hash.GetHashAndReset();
    }

    private static async Task<byte[]> ReceiveFileAsync(
        Stream source,
        string temporaryPath,
        FileHeader header,
        long alreadyProcessed,
        long totalBytes,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        var remaining = header.Length;

        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Fin de flux pendant la réception de « {header.RelativePath} ».");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            remaining -= read;
            ReportTransferProgress(
                progress,
                "Réception",
                header.RelativePath,
                alreadyProcessed + header.Length - remaining,
                totalBytes);
        }

        await destination.FlushAsync(cancellationToken);
        return hash.GetHashAndReset();
    }

    private static async Task AuthenticateClientAsync(
        Stream stream,
        PairingCode pairingCode,
        byte[] certificateFingerprint,
        CancellationToken cancellationToken)
    {
        var clientNonce = RandomNumberGenerator.GetBytes(32);
        await ProtocolFrame.WriteAsync(
            stream,
            new ClientHello(
                ProductIdentifier,
                ProtocolVersion,
                Environment.MachineName,
                Convert.ToBase64String(clientNonce)),
            cancellationToken);
        var challenge = await ProtocolFrame.ReadAsync<PairingChallenge>(
            stream,
            cancellationToken);
        var announcedFingerprint = Convert.FromBase64String(challenge.CertificateSha256);
        if (!CryptographicOperations.FixedTimeEquals(
                certificateFingerprint,
                announcedFingerprint))
        {
            throw new AuthenticationException(
                "L'identité TLS du receveur a changé pendant l'association.");
        }

        var salt = Convert.FromBase64String(challenge.Salt);
        var challengeBytes = Convert.FromBase64String(challenge.Challenge);
        var key = PairingAuthentication.DeriveKey(pairingCode, salt);
        try
        {
            var clientProof = PairingAuthentication.ComputeClientProof(
                key,
                clientNonce,
                challengeBytes,
                certificateFingerprint);
            await ProtocolFrame.WriteAsync(
                stream,
                new PairingProof(Convert.ToBase64String(clientProof)),
                cancellationToken);
            var result = await ProtocolFrame.ReadAsync<AuthenticationResult>(
                stream,
                cancellationToken);
            if (!result.Accepted || result.Proof is null)
            {
                throw new AuthenticationException(
                    result.Message ?? "Le code d'association a été refusé.");
            }

            var expectedServerProof = PairingAuthentication.ComputeServerProof(
                key,
                clientNonce,
                challengeBytes,
                certificateFingerprint);
            if (!CryptographicOperations.FixedTimeEquals(
                    expectedServerProof,
                    Convert.FromBase64String(result.Proof)))
            {
                throw new AuthenticationException(
                    "La preuve cryptographique du receveur est invalide.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<string> AuthenticateServerAsync(
        Stream stream,
        PairingCode pairingCode,
        byte[] certificateFingerprint,
        CancellationToken cancellationToken)
    {
        var hello = await ProtocolFrame.ReadAsync<ClientHello>(stream, cancellationToken);
        if (!string.Equals(hello.Product, ProductIdentifier, StringComparison.Ordinal) ||
            hello.ProtocolVersion != ProtocolVersion)
        {
            await ProtocolFrame.WriteAsync(
                stream,
                new AuthenticationResult(
                    false,
                    null,
                    "Ce PC n'utilise pas une version compatible de SmartSwitch."),
                cancellationToken);
            throw new AuthenticationException("Client SmartSwitch incompatible.");
        }

        var clientNonce = Convert.FromBase64String(hello.ClientNonce);
        if (clientNonce.Length != 32)
        {
            throw new AuthenticationException("Nonce d'association invalide.");
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var challenge = RandomNumberGenerator.GetBytes(32);
        await ProtocolFrame.WriteAsync(
            stream,
            new PairingChallenge(
                Convert.ToBase64String(salt),
                Convert.ToBase64String(challenge),
                Convert.ToBase64String(certificateFingerprint)),
            cancellationToken);
        var proof = await ProtocolFrame.ReadAsync<PairingProof>(stream, cancellationToken);
        var key = PairingAuthentication.DeriveKey(pairingCode, salt);
        try
        {
            var expectedClientProof = PairingAuthentication.ComputeClientProof(
                key,
                clientNonce,
                challenge,
                certificateFingerprint);
            var suppliedProof = Convert.FromBase64String(proof.Proof);
            if (!CryptographicOperations.FixedTimeEquals(expectedClientProof, suppliedProof))
            {
                await ProtocolFrame.WriteAsync(
                    stream,
                    new AuthenticationResult(
                        false,
                        null,
                        "Code d'association incorrect."),
                    cancellationToken);
                throw new AuthenticationException("Code d'association incorrect.");
            }

            var serverProof = PairingAuthentication.ComputeServerProof(
                key,
                clientNonce,
                challenge,
                certificateFingerprint);
            await ProtocolFrame.WriteAsync(
                stream,
                new AuthenticationResult(
                    true,
                    Convert.ToBase64String(serverProof),
                    null),
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return hello.ComputerName;
    }

    private static X509Certificate2 CreateEphemeralCertificate()
    {
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            $"CN={ProductIdentifier}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        certificateRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
        certificateRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                true));
        certificateRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(certificateRequest.PublicKey, false));

        using var generatedCertificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pfx = generatedCertificate.Export(X509ContentType.Pfx, password);
        try
        {
            // Schannel requires a persisted key handle for a server certificate.
            // The key container is deleted when this non-persisted certificate is disposed.
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                password,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private static string CreateSessionDirectory(string root, string donorComputerName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(donorComputerName
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        var baseName = $"Depuis {safeName} - {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        var candidate = Path.Combine(root, baseName);
        var suffix = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(root, $"{baseName} ({suffix++})");
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static string GetSafeDestinationPath(string sessionRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Un chemin de destination est invalide.");
        }

        var root = Path.GetFullPath(sessionRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!destination.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Une tentative de sortie du dossier de destination a été bloquée.");
        }

        return destination;
    }

    private static void ValidateManifest(TransferManifest manifest)
    {
        if (manifest.FileCount <= 0 || manifest.FileCount > MaximumFileCount)
        {
            throw new InvalidDataException("Le nombre de fichiers annoncé est invalide.");
        }

        if (manifest.TotalBytes < 0)
        {
            throw new InvalidDataException("La taille totale annoncée est invalide.");
        }
    }

    private static void ValidateFileHeader(FileHeader header, long manifestTotalBytes)
    {
        if (header.Length < 0 || header.Length > manifestTotalBytes)
        {
            throw new InvalidDataException(
                $"Taille invalide pour « {header.RelativePath} ».");
        }
    }

    private static void ValidatePort(int port)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    private static void EnsureAccepted(ProtocolAcknowledgement acknowledgement)
    {
        if (!acknowledgement.Accepted)
        {
            throw new InvalidOperationException(
                acknowledgement.Message ?? "Le PC distant a refusé l'opération.");
        }
    }

    private static void ReportTransferProgress(
        IProgress<MigrationProgress>? progress,
        string stage,
        string relativePath,
        long processedBytes,
        long totalBytes)
    {
        var percentage = totalBytes == 0
            ? 100
            : Math.Clamp(processedBytes * 100d / totalBytes, 0, 100);
        progress?.Report(new MigrationProgress(
            stage,
            $"{stage} de {Path.GetFileName(relativePath)}",
            percentage,
            processedBytes,
            totalBytes,
            relativePath));
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The original transfer error is more useful than a cleanup error.
        }
        catch (UnauthorizedAccessException)
        {
            // The original transfer error is more useful than a cleanup error.
        }
    }
}
