using System.Data;
using System.Windows.Controls;
using Accounting101.WPF.Messages;
using Accounting101.WPF.Views.Update;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF.Interfaces;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace Accounting101.WPF.ViewModels.Update;

public class UpdateBusinessViewModel : BaseViewModel
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

    public bool? Foreign
    {
        get => _foreign;
        set
        {
            _foreign = value;
            ForeignChanged();
            OnPropertyChanged();
        }
    }

    public UserControl AddressView
    {
        get => _addressView;
        set
        {
            _addressView = value;
            OnPropertyChanged();
        }
    }

    private string _businessName = string.Empty;
    private UserControl _addressView;
    private bool? _foreign;
    private readonly UpdateUSAddressView _usAddressView = new();
    private readonly UpdateForeignAddressView _foreignAddressView = new();
    private IDataStore _dataStore;
    private JoinableTaskFactory _taskFactory;
    private Business? _business;

    public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory)
    {
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        List<string> states = taskFactory.Run(dataStore.GetStatesAsync);
        _usAddressView.SetStates(states);
        _business = taskFactory.Run(dataStore.GetBusinessAsync);
        if (_business is null)
        {
            throw new DataException($"Unable to find the business in the database");
        }
        BusinessName = _business.Name;
        IAddress address = _business.Address switch
        {
            UsAddress usAddress => usAddress,
            ForeignAddress foreignAddress => foreignAddress,
            _ => throw new DataException($"Unexpected address type: {_business.Address?.GetType()}")
        };
        switch (address)
        {
            case ForeignAddress foreignAddress:
                _foreignAddressView.SetAddress(foreignAddress);
                AddressView = _foreignAddressView;
                Foreign = true;
                break;

            case UsAddress usAddress:
                _usAddressView.SetAddress(usAddress);
                AddressView = _usAddressView;
                Foreign = false;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(address));
        }
    }

    public void Save()
    {
        if (_business is null)
        {
            return;
        }
        _business.Address = AddressView switch
        {
            UpdateUSAddressView usAddressView => usAddressView.GetAddress(),
            UpdateForeignAddressView foreignAddressView => foreignAddressView.GetAddress(),
            _ => throw new DataException($"Unexpected address view type: {AddressView.GetType()}")
        };
        _business.Name = BusinessName;
        _taskFactory.Run(() => _dataStore.UpdateBusinessAsync(_business));
        WeakReferenceMessenger.Default.Send(new BusinessEditedMessage(null));
    }

    private void ForeignChanged()
    {
        if (Foreign == true)
        {
            AddressView = _foreignAddressView;
        }
        else
        {
            AddressView = _usAddressView;
        }
    }
}