using Accounting101.WPF.Messages;
using Accounting101.WPF.Models;
using Accounting101.WPF.ViewModels.Create;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.WPF.Views.Create;

public partial class CreateClientView
{
    private readonly CreateClientViewModel _viewModel;
    private readonly IDataStore _dataStore;
    private readonly JoinableTaskFactory _taskFactory;

    public CreateClientView(IDataStore dataStore, JoinableTaskFactory taskFactory)
    {
        List<string> states = taskFactory.Run(dataStore.GetStatesAsync).Order().ToList();
        _viewModel = new CreateClientViewModel(states);
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        DataContext = _viewModel;
        InitializeComponent();
    }

    public void Save()
    {
        ClientInfo info = _viewModel.GetClientInfo();
        Guid addressId = _taskFactory.Run(() => _dataStore.CreateAddressAsync(info.Address!));
        Guid personNameId = _taskFactory.Run(() => _dataStore.CreateNameAsync(info.PersonName!));
        _taskFactory.Run(() => _dataStore.CreateClientAsync(new Client
        {
            AddressId = addressId,
            BusinessName = info.BusinessName,
            PersonNameId = personNameId
        }));
        WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.ClientList));
    }
}