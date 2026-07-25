using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Infrastructure.SystemAccess;

public sealed class PrivilegeService : IPrivilegeService
{
    public bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public PrivilegeElevationResult RestartElevated(string arguments)
    {
        if (IsAdministrator)
        {
            return PrivilegeElevationResult.AlreadyElevated;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return PrivilegeElevationResult.Failed;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            });
            return PrivilegeElevationResult.RestartStarted;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return PrivilegeElevationResult.Declined;
        }
        catch (Win32Exception)
        {
            return PrivilegeElevationResult.Failed;
        }
    }
}
