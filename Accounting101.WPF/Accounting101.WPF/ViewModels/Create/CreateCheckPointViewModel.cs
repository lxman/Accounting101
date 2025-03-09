using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.ViewModels.Create;

public class CreateCheckPointViewModel : BaseViewModel
{
    public ICommand DeleteCheckpoint { get; }

    public CheckPoint? Existing
    {
        get => _existing;
        set
        {
            _existing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CheckPointExists));
        }
    }

    public bool CheckPointExists => _existing is not null;

    public DateOnly SelectedDate { private get; set; } = DateOnly.FromDateTime(DateTime.Today);

    private CheckPoint? _existing;
    private IDataStore _dataStore;
    private JoinableTaskFactory _taskFactory;
    private Guid _clientId;

    public CreateCheckPointViewModel()
    {
        DeleteCheckpoint = new RelayCommand(ClearCheckpoint);
    }

    public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
    {
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        _clientId = clientId;
        Existing = _taskFactory.Run(() => _dataStore.GetCheckpointAsync(clientId));
    }

    public void Save()
    {
        if (Existing is not null)
        {
            ClearCheckpoint();
        }
        _taskFactory.Run(() => _dataStore.SetCheckpointAsync(_clientId, SelectedDate));
        Existing = _taskFactory.Run(() => _dataStore.GetCheckpointAsync(_clientId));
    }

    private void ClearCheckpoint()
    {
        _taskFactory.Run(() => _dataStore.ClearCheckpointAsync(_existing!.ClientId));
        Existing = null;
    }
}