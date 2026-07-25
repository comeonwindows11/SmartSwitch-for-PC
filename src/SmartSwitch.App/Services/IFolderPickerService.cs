namespace SmartSwitch.App.Services;

public interface IFolderPickerService
{
    string? PickFolder(string title, string? initialDirectory = null);
}
