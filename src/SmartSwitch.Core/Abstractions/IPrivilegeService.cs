using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Abstractions;

public interface IPrivilegeService
{
    bool IsAdministrator { get; }

    PrivilegeElevationResult RestartElevated(string arguments);
}
