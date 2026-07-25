using SmartSwitch.App.Services;
using SmartSwitch.App.Utilities;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;
using SmartSwitch.Infrastructure.Network;

namespace SmartSwitch.App.ViewModels;

public sealed class DonorViewModel : OperationViewModel
{
    private readonly IMigrationEngine _migrationEngine;
    private readonly INetworkTransferService _transferService;
    private readonly IFolderPickerService _folderPicker;
    private CancellationTokenSource? _operationCancellation;
    private MigrationMode _selectedMode = MigrationMode.Advanced;
    private string _receiverHost = string.Empty;
    private string _pairingCodeText = string.Empty;
    private string _customFolderPath = string.Empty;
    private bool _includeDesktop = true;
    private bool _includeDocuments = true;
    private bool _includeDownloads = true;
    private bool _includePictures = true;
    private bool _includeMusic;
    private bool _includeVideos;
    private int _scannedFileCount;
    private long _scannedBytes;
    private string _scanSummary = "Aucun scan effectué.";
    private string _warningSummary = string.Empty;

    public DonorViewModel(
        IMigrationEngine migrationEngine,
        INetworkTransferService transferService,
        IMigrationLogger logger,
        IFolderPickerService folderPicker)
        : base(logger)
    {
        _migrationEngine = migrationEngine;
        _transferService = transferService;
        _folderPicker = folderPicker;
        SelectModeCommand = new RelayCommand<MigrationMode>(SelectMode);
        BrowseCustomFolderCommand = new RelayCommand(BrowseCustomFolder);
        ScanCommand = new AsyncRelayCommand(ScanOnlyAsync, () => IsNotBusy);
        StartTransferCommand = new AsyncRelayCommand(StartTransferAsync, () => IsNotBusy);
        CancelCommand = new RelayCommand(() => _operationCancellation?.Cancel());
    }

    public string ReceiverHost
    {
        get => _receiverHost;
        set => SetProperty(ref _receiverHost, value);
    }

    public string PairingCodeText
    {
        get => _pairingCodeText;
        set => SetProperty(ref _pairingCodeText, value);
    }

    public string CustomFolderPath
    {
        get => _customFolderPath;
        set => SetProperty(ref _customFolderPath, value);
    }

    public bool IncludeDesktop
    {
        get => _includeDesktop;
        set => SetProperty(ref _includeDesktop, value);
    }

    public bool IncludeDocuments
    {
        get => _includeDocuments;
        set => SetProperty(ref _includeDocuments, value);
    }

    public bool IncludeDownloads
    {
        get => _includeDownloads;
        set => SetProperty(ref _includeDownloads, value);
    }

    public bool IncludePictures
    {
        get => _includePictures;
        set => SetProperty(ref _includePictures, value);
    }

    public bool IncludeMusic
    {
        get => _includeMusic;
        set => SetProperty(ref _includeMusic, value);
    }

    public bool IncludeVideos
    {
        get => _includeVideos;
        set => SetProperty(ref _includeVideos, value);
    }

    public MigrationMode SelectedMode
    {
        get => _selectedMode;
        private set
        {
            if (!SetProperty(ref _selectedMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSafeMode));
            OnPropertyChanged(nameof(IsAdvancedMode));
            OnPropertyChanged(nameof(IsCustomMode));
            OnPropertyChanged(nameof(PrimaryActionText));
        }
    }

    public bool IsSafeMode => SelectedMode == MigrationMode.Safe;

    public bool IsAdvancedMode => SelectedMode == MigrationMode.Advanced;

    public bool IsCustomMode => SelectedMode == MigrationMode.Custom;

    public string PrimaryActionText =>
        IsSafeMode ? "Lancer le scan sécurisé" : "Démarrer le transfert";

    public int ScannedFileCount
    {
        get => _scannedFileCount;
        private set => SetProperty(ref _scannedFileCount, value);
    }

    public long ScannedBytes
    {
        get => _scannedBytes;
        private set => SetProperty(ref _scannedBytes, value);
    }

    public string ScanSummary
    {
        get => _scanSummary;
        private set => SetProperty(ref _scanSummary, value);
    }

    public string WarningSummary
    {
        get => _warningSummary;
        private set
        {
            if (SetProperty(ref _warningSummary, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
            }
        }
    }

    public bool HasWarnings => !string.IsNullOrWhiteSpace(WarningSummary);

    public RelayCommand<MigrationMode> SelectModeCommand { get; }

    public RelayCommand BrowseCustomFolderCommand { get; }

    public AsyncRelayCommand ScanCommand { get; }

    public AsyncRelayCommand StartTransferCommand { get; }

    public RelayCommand CancelCommand { get; }

    private void SelectMode(MigrationMode mode)
    {
        if (IsNotBusy)
        {
            SelectedMode = mode;
        }
    }

    private void BrowseCustomFolder()
    {
        var selected = _folderPicker.PickFolder(
            "Choisir un dossier personnalisé",
            CustomFolderPath);
        if (selected is not null)
        {
            CustomFolderPath = selected;
            SelectedMode = MigrationMode.Custom;
        }
    }

    private async Task ScanOnlyAsync()
    {
        _operationCancellation = new CancellationTokenSource();
        BeginOperation("Scan", "Préparation de l'analyse en lecture seule…");
        try
        {
            var summary = await ScanAsync(_operationCancellation.Token);
            CompleteOperation(
                "Scan terminé",
                $"{summary.Files.Count:N0} fichier(s), {FormatUtilities.FormatBytes(summary.TotalBytes)}.");
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

    private async Task StartTransferAsync()
    {
        _operationCancellation = new CancellationTokenSource();
        BeginOperation("Préparation", "Validation de la sélection…");
        try
        {
            if (IsSafeMode)
            {
                var safeSummary = await ScanAsync(_operationCancellation.Token);
                CompleteOperation(
                    "Scan sécurisé terminé",
                    $"{safeSummary.Files.Count:N0} fichier(s) analysé(s); aucune donnée n'a été modifiée.");
                return;
            }

            if (string.IsNullOrWhiteSpace(ReceiverHost))
            {
                throw new InvalidOperationException(
                    "Entrez l'adresse IP ou le nom du PC receveur.");
            }

            if (!PairingCode.TryParse(PairingCodeText, out var pairingCode))
            {
                throw new InvalidOperationException(
                    "Entrez le code à 8 chiffres affiché sur le PC receveur.");
            }

            var summary = await ScanAsync(_operationCancellation.Token);
            if (summary.Files.Count == 0)
            {
                throw new InvalidOperationException(
                    "Le scan n'a trouvé aucun fichier à transférer.");
            }

            StatusMessage = "Connexion";
            StatusDetail = "Association sécurisée avec le PC receveur…";
            var result = await _transferService.SendAsync(
                new SendTransferRequest(
                    ReceiverHost.Trim(),
                    NetworkTransferService.DefaultPort,
                    pairingCode,
                    summary.Files),
                CreateProgressReporter(),
                _operationCancellation.Token);
            CompleteOperation(
                "Migration terminée",
                $"{result.FileCount:N0} fichier(s) transféré(s) avec intégrité vérifiée.");
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

    private async Task<MigrationScanSummary> ScanAsync(CancellationToken cancellationToken)
    {
        var categories = BuildSelectedCategories();
        if (categories.Count == 0)
        {
            throw new InvalidOperationException(
                "Sélectionnez au moins une catégorie de fichiers.");
        }

        var customPaths = IsCustomMode && !string.IsNullOrWhiteSpace(CustomFolderPath)
            ? new[] { CustomFolderPath }
            : [];
        var request = new MigrationRequest(
            SelectedMode,
            categories,
            customPaths);
        var summary = await _migrationEngine.ScanAsync(
            request,
            CreateProgressReporter(),
            cancellationToken);
        ScannedFileCount = summary.Files.Count;
        ScannedBytes = summary.TotalBytes;
        ScanSummary =
            $"{ScannedFileCount:N0} fichier(s) • {FormatUtilities.FormatBytes(ScannedBytes)}";
        WarningSummary = summary.Warnings.Count == 0
            ? string.Empty
            : $"{summary.Warnings.Count:N0} élément(s) inaccessible(s) ou ignoré(s).";
        return summary;
    }

    private HashSet<MigrationCategory> BuildSelectedCategories()
    {
        var categories = new HashSet<MigrationCategory>();
        AddIf(IncludeDesktop, MigrationCategory.Desktop);
        AddIf(IncludeDocuments, MigrationCategory.Documents);
        AddIf(IncludeDownloads, MigrationCategory.Downloads);
        AddIf(IncludePictures, MigrationCategory.Pictures);
        AddIf(IncludeMusic, MigrationCategory.Music);
        AddIf(IncludeVideos, MigrationCategory.Videos);
        AddIf(
            IsCustomMode && !string.IsNullOrWhiteSpace(CustomFolderPath),
            MigrationCategory.CustomFiles);
        return categories;

        void AddIf(bool condition, MigrationCategory category)
        {
            if (condition)
            {
                categories.Add(category);
            }
        }
    }
}
