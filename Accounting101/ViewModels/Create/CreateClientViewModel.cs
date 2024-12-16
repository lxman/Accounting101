using System.Windows.Controls;
using Accounting101.Messages;
using Accounting101.ViewModels.Single;
using Accounting101.Views.Create;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels.Create
{
    public class CreateClientViewModel : BaseViewModel, IRecipient<SaveMessage>
    {
        public event EventHandler? ClientCreated;

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

        public CreateClientViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            WeakReferenceMessenger.Default.Register(this);
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            Client = new Client();
            PersonName = new PersonName();
            AddressView = new CreateUSAddressView(_dataStore, _taskFactory);
            _addressDataContext = AddressView.DataContext;
        }

        public void Receive(SaveMessage message)
        {
            if (message.Value != WindowType.CreateClient)
            {
                return;
            }

            _taskFactory.Run(SaveAsync);
            Messenger.Send(new ChangeScreenMessage(WindowType.ClientList));
        }

        public async Task<bool> SaveAsync()
        {
            Guid personNameId = await _dataStore.CreateNameAsync(_personName);
            Guid addressId = _foreignCheckboxState
                ? await _dataStore.CreateAddressAsync(((ForeignAddressViewModel)_addressDataContext).Address)
                : await _dataStore.CreateAddressAsync(((USAddressViewModel)_addressDataContext).Address);
            _client.AddressId = addressId;
            _client.PersonNameId = personNameId;
            ClientCreated?.Invoke(this, EventArgs.Empty);
            return await _dataStore.CreateClientAsync(_client) != Guid.Empty;
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