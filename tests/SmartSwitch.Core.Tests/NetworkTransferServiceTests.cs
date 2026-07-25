using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SmartSwitch.Core.Models;
using SmartSwitch.Infrastructure.Network;

namespace SmartSwitch.Core.Tests;

public sealed class NetworkTransferServiceTests
{
    [Fact(Timeout = 30_000)]
    public async Task TransferAsyncCopiesFileAndPreservesIntegrity()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"SmartSwitch.Tests-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var destinationRoot = Path.Combine(testRoot, "destination");
        Directory.CreateDirectory(sourceRoot);
        var sourcePath = Path.Combine(sourceRoot, "payload.bin");
        var payload = RandomNumberGenerator.GetBytes(512 * 1024 + 37);
        await File.WriteAllBytesAsync(sourcePath, payload);
        var fileInfo = new FileInfo(sourcePath);
        using var logger = new TestLogger();
        var service = new NetworkTransferService(logger);
        var code = PairingCode.Generate();
        var port = GetAvailablePort();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            var receiveTask = service.ReceiveAsync(
                new ReceiveTransferRequest(
                    port,
                    code,
                    destinationRoot,
                    ListenOnLoopbackOnly: true),
                cancellationToken: timeout.Token);
            TransferResult sendResult;
            try
            {
                sendResult = await service.SendAsync(
                    new SendTransferRequest(
                        IPAddress.Loopback.ToString(),
                        port,
                        code,
                        [
                            new MigrationFile(
                                "test",
                                sourcePath,
                                Path.Combine("Documents", "nested", "payload.bin"),
                                fileInfo.Length,
                                fileInfo.LastWriteTimeUtc),
                        ]),
                    cancellationToken: timeout.Token);
            }
            catch (Exception sendException)
            {
                try
                {
                    await receiveTask;
                }
                catch (Exception receiveException)
                {
                    throw new AggregateException(sendException, receiveException);
                }

                throw;
            }
            var receiveResult = await receiveTask;

            Assert.True(sendResult.Succeeded);
            Assert.True(receiveResult.Succeeded);
            Assert.Equal(1, receiveResult.FileCount);
            var receivedPath = Directory
                .EnumerateFiles(destinationRoot, "payload.bin", SearchOption.AllDirectories)
                .Single();
            Assert.Equal(payload, await File.ReadAllBytesAsync(receivedPath, timeout.Token));
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void DeleteTestDirectory(string testRoot)
    {
        var fullPath = Path.GetFullPath(testRoot);
        var expectedRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith(
                "SmartSwitch.Tests-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refus de supprimer un dossier de test inattendu.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
