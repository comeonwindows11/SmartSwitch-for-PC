using System.Collections.ObjectModel;
using System.Windows;
using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;

namespace SmartSwitch.App.ViewModels;

public abstract class OperationViewModel : ObservableObject
{
    private double _progressPercentage;
    private string _statusMessage = "Prêt";
    private string _statusDetail = "Configurez l'opération pour commencer.";
    private bool _isBusy;

    protected OperationViewModel(IMigrationLogger logger)
    {
        logger.EntryWritten += OnLogEntryWritten;
    }

    public ObservableCollection<MigrationLogEntry> Logs { get; } = [];

    public double ProgressPercentage
    {
        get => _progressPercentage;
        protected set => SetProperty(ref _progressPercentage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        protected set => SetProperty(ref _statusMessage, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        protected set => SetProperty(ref _statusDetail, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    protected IProgress<MigrationProgress> CreateProgressReporter() =>
        new Progress<MigrationProgress>(progress =>
        {
            ProgressPercentage = progress.Percentage;
            StatusMessage = progress.Stage;
            StatusDetail = progress.Message;
        });

    protected void BeginOperation(string title, string detail)
    {
        IsBusy = true;
        ProgressPercentage = 0;
        StatusMessage = title;
        StatusDetail = detail;
    }

    protected void CompleteOperation(string title, string detail)
    {
        ProgressPercentage = 100;
        StatusMessage = title;
        StatusDetail = detail;
        IsBusy = false;
    }

    protected void FailOperation(Exception exception)
    {
        StatusMessage = "Opération interrompue";
        StatusDetail = exception.Message;
        IsBusy = false;
    }

    protected void CancelOperation()
    {
        StatusMessage = "Opération annulée";
        StatusDetail = "Aucune autre donnée ne sera traitée.";
        IsBusy = false;
    }

    private void OnLogEntryWritten(object? sender, MigrationLogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.InvokeAsync(() =>
        {
            Logs.Insert(0, entry);
            while (Logs.Count > 100)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        });
    }
}
