using System.Diagnostics;
using System.IO;
using SmartSwitch.App.Services;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;
using SmartSwitch.Infrastructure.Network;

namespace SmartSwitch.App.ViewModels;

public sealed class ReceiverViewModel : OperationViewModel
{
    private readonly INetworkTransferService _transferService;
    private readonly IFolderPickerService _folderPicker;
    private CancellationTokenSource? _operationCancellation;
    private PairingCode _pairingCode = PairingCode.Generate();
    private string _destinationRoot;
    private string _lastReceivedPath = string.Empty;

    public ReceiverViewModel(
        INetworkTransferService transferService,
        INetworkInformationService networkInformation,
        IMigrationLogger logger,
        IFolderPickerService folderPicker)
        : base(logger)
    {
        _transferService = transferService;
        _folderPicker = folderPicker;
        _destinationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SmartSwitch Imports");
        var addresses = networkInformation.GetLocalIpv4Addresses();
        LocalAddresses = addresses.Count == 0
            ? "Aucune adresse IPv4 active détectée"
            : string.Join("  •  ", addresses);
        PrimaryAddress = addresses.Count > 0
            ? addresses[0]
            : "Adresse réseau indisponible";

        StartListeningCommand = new AsyncRelayCommand(StartListeningAsync, () => IsNotBusy);
        CancelCommand = new RelayCommand(() => _operationCancellation?.Cancel());
        RegenerateCodeCommand = new RelayCommand(RegenerateCode, () => IsNotBusy);
        BrowseDestinationCommand = new RelayCommand(BrowseDestination);
        OpenDestinationCommand = new RelayCommand(OpenDestination);
    }

    public string PairingCodeText => _pairingCode.DisplayValue;

    public string LocalAddresses { get; }

    public string PrimaryAddress { get; }

    public int Port => NetworkTransferService.DefaultPort;

    public string DestinationRoot
    {
        get => _destinationRoot;
        set => SetProperty(ref _destinationRoot, value);
    }

    public string LastReceivedPath
    {
        get => _lastReceivedPath;
        private set
        {
            if (SetProperty(ref _lastReceivedPath, value))
            {
                OnPropertyChanged(nameof(HasReceivedFiles));
            }
        }
    }

    public bool HasReceivedFiles => !string.IsNullOrWhiteSpace(LastReceivedPath);

    public AsyncRelayCommand StartListeningCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand RegenerateCodeCommand { get; }

    public RelayCommand BrowseDestinationCommand { get; }

    public RelayCommand OpenDestinationCommand { get; }

    private async Task StartListeningAsync()
    {
        _operationCancellation = new CancellationTokenSource();
        BeginOperation("Association", "Ouverture du canal de réception sécurisé…");
        try
        {
            if (string.IsNullOrWhiteSpace(DestinationRoot))
            {
                throw new InvalidOperationException(
                    "Choisissez un dossier de destination.");
            }

            var result = await _transferService.ReceiveAsync(
                new ReceiveTransferRequest(
                    NetworkTransferService.DefaultPort,
                    _pairingCode,
                    DestinationRoot),
                CreateProgressReporter(),
                _operationCancellation.Token);
            LastReceivedPath = result.DestinationPath;
            CompleteOperation(
                "Migration reçue",
                $"{result.FileCount:N0} fichier(s) reçus depuis {result.PeerComputerName}.");
        }
        catch (OperationCanceledException)
        {
            CancelOperation();
        }
        catch (Exception exception)
        {
            FailOperation(exception);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private void RegenerateCode()
    {
        if (!IsNotBusy)
        {
            return;
        }

        _pairingCode = PairingCode.Generate();
        OnPropertyChanged(nameof(PairingCodeText));
        StatusMessage = "Nouveau code généré";
        StatusDetail = "Communiquez uniquement ce code au PC donneur.";
    }

    private void BrowseDestination()
    {
        if (!IsNotBusy)
        {
            return;
        }

        var selected = _folderPicker.PickFolder(
            "Choisir le dossier de réception",
            DestinationRoot);
        if (selected is not null)
        {
            DestinationRoot = selected;
        }
    }

    private void OpenDestination()
    {
        var path = HasReceivedFiles ? LastReceivedPath : DestinationRoot;
        if (!Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }
}
