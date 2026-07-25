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
    private const int ProtocolVersion = 2;
    private const string ApplicationProtocol = "smartswitch/2";
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
                ApplicationProtocols = [new SslApplicationProtocol(ApplicationProtocol)],
            },
            cancellationToken);
        EnsureNegotiatedApplicationProtocol(secureStream);

        var remoteCertificate = secureStream.RemoteCertificate ??
            throw new AuthenticationException("Le receveur n'a fourni aucun certificat TLS.");
        var certificateFingerprint = SHA256.HashData(remoteCertificate.GetRawCertData());
        await AuthenticateClientAsync(
            secureStream,
            request.PairingCode,
            certificateFingerprint,
            cancellationToken);

        var totalBytes = request.Files.Sum(file => file.Length);
        var sessionId = request.SessionId.GetValueOrDefault(Guid.NewGuid());
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("L'identifiant de session est invalide.", nameof(request));
        }

        await ProtocolFrame.WriteAsync(
            secureStream,
            new TransferManifest(
                Environment.MachineName,
                request.Files.Count,
                totalBytes,
                sessionId,
                request.Preflight),
            cancellationToken);
        var manifestAcknowledgement =
            await ProtocolFrame.ReadAsync<ProtocolAcknowledgement>(
                secureStream,
                cancellationToken);
        EnsureAccepted(manifestAcknowledgement);

        var processedBytes = 0L;
        var resumedBytes = 0L;
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

            var expectedHash = await GetExpectedHashAsync(file, cancellationToken);
            await ProtocolFrame.WriteAsync(
                secureStream,
                new FileHeader(
                    file.RelativePath,
                    file.Length,
                    file.LastWriteTimeUtc,
                    expectedHash),
                cancellationToken);
            var resume = await ProtocolFrame.ReadAsync<FileResumeResponse>(
                secureStream,
                cancellationToken);
            if (!resume.Accepted)
            {
                throw new InvalidOperationException(
                    resume.Message ?? $"Le receveur a refusé « {file.RelativePath} ».");
            }

            if (resume.Offset is < 0 or > file.Length)
            {
                throw new InvalidDataException(
                    $"Le receveur a retourné un point de reprise invalide pour « {file.RelativePath} ».");
            }

            await SendFileAsync(
                secureStream,
                file,
                resume.Offset,
                processedBytes,
                totalBytes,
                progress,
                cancellationToken);
            await ProtocolFrame.WriteAsync(
                secureStream,
                new FileTrailer(expectedHash),
                cancellationToken);

            var acknowledgement = await ProtocolFrame.ReadAsync<ProtocolAcknowledgement>(
                secureStream,
                cancellationToken);
            EnsureAccepted(acknowledgement);
            processedBytes += file.Length;
            resumedBytes += resume.Offset;
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
            [],
            sessionId,
            request.Preflight,
            resumedBytes);
    }

    public async Task<TransferResult> ReceiveAsync(
        ReceiveTransferRequest request,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePort(request.Port);
        if (request.PairingExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("Le code d'association a expiré.");
        }

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
                    ApplicationProtocols = [new SslApplicationProtocol(ApplicationProtocol)],
                },
                cancellationToken);
            EnsureNegotiatedApplicationProtocol(secureStream);

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

            var preflight = ValidateReceiverPreflight(manifest, destinationRoot, request.RequirePreOs);
            if (!preflight.Accepted)
            {
                await ProtocolFrame.WriteAsync(secureStream, preflight, cancellationToken);
                throw new InvalidOperationException(preflight.Message);
            }

            var sessionDirectory = CreateSessionDirectory(
                destinationRoot,
                manifest.ComputerName,
                manifest.SessionId);
            await ProtocolFrame.WriteAsync(secureStream, preflight, cancellationToken);

            var receivedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var receivedBytes = 0L;
            var announcedBytes = 0L;
            var resumedBytes = 0L;
            for (var index = 0; index < manifest.FileCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var header = await ProtocolFrame.ReadAsync<FileHeader>(
                    secureStream,
                    cancellationToken);
                ValidateFileHeader(header, manifest.TotalBytes);
                announcedBytes = checked(announcedBytes + header.Length);
                if (announcedBytes > manifest.TotalBytes)
                {
                    throw new InvalidDataException(
                        "Les fichiers annoncés dépassent la taille totale du manifeste.");
                }

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
                    var offset = DetermineResumeOffset(
                        destinationPath,
                        temporaryPath,
                        header);
                    var receivePath = File.Exists(destinationPath)
                        ? destinationPath
                        : temporaryPath;
                    await ProtocolFrame.WriteAsync(
                        secureStream,
                        new FileResumeResponse(true, offset),
                        cancellationToken);
                    actualHash = await ReceiveFileAsync(
                        secureStream,
                        receivePath,
                        header,
                        offset,
                        receivedBytes,
                        manifest.TotalBytes,
                        progress,
                        cancellationToken);
                    var trailer = await ProtocolFrame.ReadAsync<FileTrailer>(
                        secureStream,
                        cancellationToken);
                    var expectedHash = Convert.FromHexString(header.Sha256);
                    var trailerHash = Convert.FromHexString(trailer.Sha256);
                    if (!CryptographicOperations.FixedTimeEquals(expectedHash, trailerHash) ||
                        !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                    {
                        TryDeletePartialFile(temporaryPath);
                        throw new InvalidDataException(
                            $"Le contrôle d'intégrité a échoué pour « {header.RelativePath} ».");
                    }

                    if (!File.Exists(destinationPath))
                    {
                        File.Move(temporaryPath, destinationPath, overwrite: false);
                        File.SetLastWriteTimeUtc(destinationPath, header.LastWriteTimeUtc.UtcDateTime);
                    }

                    resumedBytes += offset;
                }
                catch (InvalidDataException)
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

            if (announcedBytes != manifest.TotalBytes)
            {
                throw new InvalidDataException(
                    "La taille cumulée des fichiers ne correspond pas au manifeste.");
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
                [],
                manifest.SessionId,
                manifest.Preflight,
                resumedBytes);
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

    private static async Task SendFileAsync(
        Stream destination,
        MigrationFile file,
        long resumeOffset,
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

        if (resumeOffset > source.Length)
        {
            throw new InvalidDataException(
                $"Le point de reprise dépasse la taille de « {file.SourcePath} ».");
        }

        source.Position = resumeOffset;
        var buffer = new byte[BufferSize];
        var fileBytes = resumeOffset;
        while (fileBytes < file.Length)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Fin inattendue du fichier « {file.SourcePath} ».");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            fileBytes += read;
            ReportTransferProgress(
                progress,
                "Envoi",
                file.RelativePath,
                alreadyProcessed + fileBytes,
                totalBytes);
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReceiveFileAsync(
        Stream source,
        string temporaryPath,
        FileHeader header,
        long resumeOffset,
        long alreadyProcessed,
        long totalBytes,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            temporaryPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (destination.Length != resumeOffset)
        {
            throw new InvalidDataException(
                $"Le fichier partiel de « {header.RelativePath} » est incohérent.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        destination.Position = 0;
        var prefixRemaining = resumeOffset;
        while (prefixRemaining > 0)
        {
            var read = await destination.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, prefixRemaining)),
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Le fichier partiel de « {header.RelativePath} » est incomplet.");
            }

            hash.AppendData(buffer, 0, read);
            prefixRemaining -= read;
        }

        destination.Position = resumeOffset;
        var remaining = header.Length - resumeOffset;

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

    private static string CreateSessionDirectory(
        string root,
        string donorComputerName,
        Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new InvalidDataException("L'identifiant de session est invalide.");
        }

        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(donorComputerName
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        var directory = Path.Combine(root, $"Depuis {safeName} - {sessionId:N}");
        Directory.CreateDirectory(directory);
        return directory;
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

        if (manifest.SessionId == Guid.Empty)
        {
            throw new InvalidDataException("L'identifiant de session est invalide.");
        }
    }

    private static void ValidateFileHeader(FileHeader header, long manifestTotalBytes)
    {
        if (header.Length < 0 || header.Length > manifestTotalBytes)
        {
            throw new InvalidDataException(
                $"Taille invalide pour « {header.RelativePath} ».");
        }

        if (string.IsNullOrWhiteSpace(header.Sha256) || header.Sha256.Length != 64)
        {
            throw new InvalidDataException(
                $"Empreinte invalide pour « {header.RelativePath} ».");
        }

        try
        {
            _ = Convert.FromHexString(header.Sha256);
        }
        catch (FormatException)
        {
            throw new InvalidDataException(
                $"Empreinte invalide pour « {header.RelativePath} ».");
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

    private static void EnsureNegotiatedApplicationProtocol(SslStream stream)
    {
        if (!string.Equals(
                stream.NegotiatedApplicationProtocol.Protocol,
                ApplicationProtocol,
                StringComparison.Ordinal))
        {
            throw new AuthenticationException("Le protocole SmartSwitch négocié est invalide.");
        }
    }

    private static ProtocolAcknowledgement ValidateReceiverPreflight(
        TransferManifest manifest,
        string destinationRoot,
        bool requirePreOs)
    {
        if (manifest.Preflight is { PackageSchemaVersion: not MigrationPackageSchema.CurrentVersion })
        {
            return new ProtocolAcknowledgement(
                false,
                "La version de package annoncée n'est pas prise en charge.");
        }

        var root = Path.GetPathRoot(destinationRoot);
        if (string.IsNullOrWhiteSpace(root))
        {
            return new ProtocolAcknowledgement(
                false,
                "Le volume de réception est introuvable.");
        }

        var availableBytes = new DriveInfo(root).AvailableFreeSpace;
        var requiredBytes = checked((long)Math.Ceiling(manifest.TotalBytes * 1.25));
        if (availableBytes < requiredBytes)
        {
            return new ProtocolAcknowledgement(
                false,
                "L'espace disque du receveur est insuffisant.");
        }

        if (manifest.Preflight is { } preflight &&
            !string.Equals(
                preflight.DonorOsArchitecture,
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return new ProtocolAcknowledgement(
                false,
                "Les architectures système des deux PC ne sont pas compatibles.");
        }

        if (requirePreOs && manifest.Preflight?.RequiresPreOs != true)
        {
            return new ProtocolAcknowledgement(
                false,
                "Le donneur n'a pas préparé un package compatible pré-OS.");
        }

        return new ProtocolAcknowledgement(true);
    }

    private static long DetermineResumeOffset(
        string destinationPath,
        string temporaryPath,
        FileHeader header)
    {
        if (File.Exists(destinationPath))
        {
            var destinationInfo = new FileInfo(destinationPath);
            if (destinationInfo.Length != header.Length ||
                !string.Equals(
                    ComputeSha256(destinationPath),
                    header.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Le fichier reçu « {header.RelativePath} » ne correspond pas à la session.");
            }

            return header.Length;
        }

        if (!File.Exists(temporaryPath))
        {
            return 0;
        }

        var temporaryLength = new FileInfo(temporaryPath).Length;
        if (temporaryLength > header.Length)
        {
            TryDeletePartialFile(temporaryPath);
            return 0;
        }

        return temporaryLength;
    }

    private static async Task<string> GetExpectedHashAsync(
        MigrationFile file,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(file.Sha256) &&
            file.Sha256.Length == 64)
        {
            return file.Sha256.ToUpperInvariant();
        }

        await using var source = new FileStream(
            file.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
