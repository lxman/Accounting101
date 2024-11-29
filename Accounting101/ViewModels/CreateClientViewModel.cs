using System.Windows.Controls;
using Accounting101.Interfaces;
using Accounting101.Views.Create;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8602 // Dereference of a possibly null reference.
#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels
{
    public class CreateClientViewModel : BaseViewModel, ISavable
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

        public PersonName? PersonName
        {
            get => _personName;
            set => SetField(ref _personName, value);
        }

        public Client? Client
        {
            get => _client;
            set => SetField(ref _client, value);
        }

        private Client? _client;
        private PersonName? _personName;
        private UserControl _addressView;
        private bool _foreignCheckboxState;
        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;

        public CreateClientViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            _dataStore = dataStore;
            _client = new Client();
            _personName = new PersonName();
            AddressView = new CreateUSAddressView(_dataStore, taskFactory);
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

        public async Task<bool> SaveAsync()
        {
            Guid personNameId = await _dataStore.CreateNameAsync(_personName);
            Guid addressId = _foreignCheckboxState
                ? await _dataStore.CreateAddressAsync(((ForeignAddressViewModel)AddressView.DataContext).Address)
                : await _dataStore.CreateAddressAsync(((USAddressViewModel)AddressView.DataContext).Address);
            _client.AddressId = addressId;
            _client.PersonNameId = personNameId;
            return (await _dataStore.CreateClientAsync(_client)) != Guid.Empty;
        }
    }
}