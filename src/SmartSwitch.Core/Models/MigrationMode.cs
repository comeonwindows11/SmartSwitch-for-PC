namespace SmartSwitch.Core.Models;

/// <summary>
/// Controls how deeply SmartSwitch may inspect or change the system.
/// </summary>
public enum MigrationMode
{
    Safe,
    Advanced,
    Custom,
}
