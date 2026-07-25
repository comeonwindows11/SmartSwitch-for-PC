using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SmartSwitch.Core.Abstractions;

namespace SmartSwitch.Infrastructure.SystemAccess;

public sealed class NetworkInformationService : INetworkInformationService
{
    public IReadOnlyList<string> GetLocalIpv4Addresses()
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address.Address))
                {
                    addresses.Add(address.Address.ToString());
                }
            }
        }

        return addresses
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
