using System.Windows.Controls;
using Accounting101.WPF.Messages;
using Accounting101.WPF.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.Views.Update;

[ObservableObject]
public partial class UpdateClientView
{
    public string BusinessName
    {
        get => _businessName;
        set
        {
            _businessName = value;
            OnPropertyChanged();
        }
    }

    public PersonName? PersonName
    {
        get => _personName;
        set
        {
            _personName = value;
            OnPropertyChanged();
        }
    }

    public UserControl? AddressView
    {
        get => _addressView;
        set
        {
            if (_addressView == value)
            {
                return;
            }
            _addressView = value;
            OnPropertyChanged();
        }
    }

    public bool? Foreign
    {
        get => _foreign;
        set
        {
            if (_foreign == value)
            {
                return;
            }

            _foreign = value;
            ForeignChanged();
            OnPropertyChanged();
        }
    }

    private string _businessName = string.Empty;
    private PersonName? _personName;
    private bool? _foreign = false;
    private UserControl? _addressView;
    private readonly UpdateUSAddressView _usAddressView = new();
    private readonly UpdateForeignAddressView _foreignAddressView = new();
    private ClientWithInfo _client;
    private IDataStore _dataStore;
    private JoinableTaskFactory _taskFactory;

    public UpdateClientView()
    {
        DataContext = this;
        InitializeComponent();
    }

    public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client, List<string> states)
    {
        _client = client;
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        BusinessName = client.BusinessName;
        PersonName = client.Name;
        AddressView = client.Address switch
        {
            UsAddress => _usAddressView,
            ForeignAddress => _foreignAddressView,
            _ => null
        };
        if (AddressView is UpdateUSAddressView usAddressView)
        {
            usAddressView.SetAddress(client.Address as UsAddress);
            usAddressView.SetStates(states);
        }
        Foreign = AddressView switch
        {
            UpdateUSAddressView => false,
            UpdateForeignAddressView => true,
            _ => null
        };
    }

    public void Save()
    {
        ClientInfo client = GetClient();
        Client c = new() { Id = _client.Id, AddressId = client.AddressId, PersonNameId = client.PersonNameId, BusinessName = client.BusinessName };
        _taskFactory.Run(() =>
            _dataStore.UpdateClientAsync(new ClientWithInfo(c, client.PersonName!, client.Address!)));
        WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.ClientList));
    }

    public ClientInfo GetClient()
    {
        return new ClientInfo
        {
            Id = _client.Id,
            PersonNameId = _client.PersonNameId,
            AddressId = _client.AddressId,
            BusinessName = BusinessName,
            PersonName = PersonName,
            Address = AddressView switch
            {
                UpdateUSAddressView usAddressView => usAddressView.GetAddress(),
                UpdateForeignAddressView foreignAddressView => foreignAddressView.GetAddress(),
                _ => null
            }
        };
    }

    private void ForeignChanged()
    {
        AddressView = Foreign == true ? _foreignAddressView : _usAddressView;
    }
}