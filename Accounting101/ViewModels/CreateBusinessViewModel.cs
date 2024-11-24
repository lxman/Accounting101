using System.Windows.Controls;
using Accounting101.Interfaces;
using Accounting101.Views.Create;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels
{
    public class CreateBusinessViewModel : BaseViewModel, ISavable
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

        public Business Business
        {
            get => _business;
            set => SetField(ref _business, value);
        }

        private Business _business;
        private bool _foreignCheckboxState;
        private readonly IDataStore _dataStore;
        private UserControl _addressView;

        public CreateBusinessViewModel(IDataStore dataStore)
        {
            _dataStore = dataStore;
            Business? found = _dataStore.GetBusiness();
            Business ??= found ?? new Business();
            _foreignCheckboxState = Business.Address is ForeignAddress;
            if (_foreignCheckboxState)
            {
                Business.Address = new ForeignAddress();
                AddressView = new CreateForeignAddressView();
            }
            else
            {
                Business.Address = new UsAddress();
                AddressView = new CreateUSAddressView(_dataStore);
            }
        }

        public bool Save()
        {
            Guid addressId = _dataStore.CreateAddress(Business.Address);
            _dataStore.CreateBusiness(Business);
            return false;
        }

        private void ForeignCheckboxChangeState(bool state)
        {
            if (state)
            {
                Business.Address = new ForeignAddress();
                AddressView = new CreateForeignAddressView();
            }
            else
            {
                Business.Address = new UsAddress();
                AddressView = new CreateUSAddressView(_dataStore);
            }
        }
    }
}
