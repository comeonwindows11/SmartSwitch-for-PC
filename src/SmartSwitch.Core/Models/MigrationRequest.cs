namespace SmartSwitch.Core.Models;

public sealed record MigrationRequest(
    MigrationMode Mode,
    IReadOnlySet<MigrationCategory> Categories,
    IReadOnlyList<string> CustomPaths,
    IReadOnlySet<string>? EnabledModuleIds = null);
