namespace SmartSwitch.Setup.Services;

internal sealed record InstallManifest(
    string Version,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Shortcuts);

public sealed record InstallerProgress(double Percentage, string Message);
