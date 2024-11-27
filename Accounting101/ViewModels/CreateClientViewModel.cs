using System.Windows.Controls;
using Accounting101.Interfaces;
using Accounting101.Views.Create;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
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

        public CreateClientViewModel(IDataStore dataStore)
        {
            _dataStore = dataStore;
            _client = new Client();
            _personName = new PersonName();
            AddressView = new CreateUSAddressView(_dataStore);
        }

        private void ForeignCheckboxChangeState(bool state)
        {
            if (state)
            {
                AddressView = new CreateForeignAddressView();
            }
            else
            {
                AddressView = new CreateUSAddressView(_dataStore);
            }
        }

        public bool Save()
        {
            Guid personNameId = _dataStore.CreateName(_personName);
            Guid addressId = _foreignCheckboxState
                ? _dataStore.CreateAddress(((ForeignAddressViewModel)AddressView.DataContext).Address)
                : _dataStore.CreateAddress(((USAddressViewModel)AddressView.DataContext).Address);
            _client.AddressId = addressId;
            _client.PersonNameId = personNameId;
            return _dataStore.CreateClient(_client) != Guid.Empty;
        }
    }
}
