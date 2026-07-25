using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Abstractions;

public interface INetworkTransferService
{
    Task<TransferResult> SendAsync(
        SendTransferRequest request,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<TransferResult> ReceiveAsync(
        ReceiveTransferRequest request,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
