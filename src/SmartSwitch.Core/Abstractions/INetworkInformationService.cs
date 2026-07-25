namespace SmartSwitch.Core.Abstractions;

public interface INetworkInformationService
{
    IReadOnlyList<string> GetLocalIpv4Addresses();
}
