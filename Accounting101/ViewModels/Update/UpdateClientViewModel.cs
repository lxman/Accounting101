using System.Windows.Controls;
using Accounting101.Messages;
using Accounting101.ViewModels.Single;
using Accounting101.Views.Create;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Interfaces;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels.Update
{
    public class UpdateClientViewModel : BaseViewModel, IRecipient<SaveMessage>
    {
        public UserControl AddressView
        {
            get => _addressView;
            private set => SetField(ref _addressView, value);
        }

        public bool ForeignCheckboxState
        {
            get => _foreignCheckboxState;
            set
            {
                ForeignCheckboxChangeState(value);
                _foreignCheckboxState = value;
            }
        }

        public PersonName PersonName
        {
            get => _personName;
            set => SetField(ref _personName, value);
        }

        public Client Client
        {
            get => _client;
            set => SetField(ref _client, value);
        }

        private Client _client;
        private PersonName _personName;
        private UserControl _addressView;
        private bool _foreignCheckboxState;
        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;
        private readonly object _addressDataContext;

        public UpdateClientViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            WeakReferenceMessenger.Default.Register(this);
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            Client? c = _taskFactory.Run(() => _dataStore.FindClientByIdAsync(clientId));
            if (c is null)
            {
                Client = new Client();
                PersonName = new PersonName();
                AddressView = new CreateUSAddressView(_dataStore, _taskFactory);
            }
            else
            {
                Client = c;
                PersonName? pn = _taskFactory.Run(() => _dataStore.FindNameByIdAsync(c.PersonNameId));
                if (pn is null)
                {
                    PersonName = new PersonName();
                }
                else
                {
                    PersonName = pn;
                    IAddress? address = _taskFactory.Run(() => _dataStore.FindAddressByIdAsync(c.AddressId));
                    if (address is null)
                    {
                        AddressView = new CreateUSAddressView(_dataStore, _taskFactory);
                    }
                    else
                    {
                        // TODO: Fix foreign address view creation
                        AddressView = address is ForeignAddress
                            ? new CreateForeignAddressView()
                            : new CreateUSAddressView(_dataStore, _taskFactory, address.Id);
                    }
                }
            }
            _addressDataContext = AddressView.DataContext;
        }

        public void Receive(SaveMessage message)
        {
            if (message.Value != WindowType.EditClient)
            {
                return;
            }

            _taskFactory.Run(SaveAsync);
            Messenger.Send(new ChangeScreenMessage(WindowType.ClientList));
        }

        public async Task<bool?> SaveAsync()
        {
            _ = await _dataStore.UpdateNameAsync(_personName);
            return _foreignCheckboxState
                ? await _dataStore.UpdateAddressAsync(((ForeignAddressViewModel)_addressDataContext).Address)
                : await _dataStore.UpdateAddressAsync(((USAddressViewModel)_addressDataContext).Address);
        }

        private void ForeignCheckboxChangeState(bool state)
        {
            if (state)
            {
                AddressView = new CreateForeignAddressView();
            }
            else
            {
                AddressView = new CreateUSAddressView(_dataStore, _taskFactory);
            }
        }
    }
}