using System.Windows.Controls;
using Accounting101.Interfaces;
using Accounting101.Views.Create;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

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

        public Business? Business
        {
            get => _business;
            set => SetField(ref _business, value);
        }

        private Business? _business;
        private bool _foreignCheckboxState;
        private readonly IDataStore _dataStore;
        private UserControl _addressView;
        private readonly JoinableTaskFactory _taskFactory;

        public CreateBusinessViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            Business? found = taskFactory.Run(() => _dataStore.GetBusinessAsync());
            Business ??= found ?? new Business();
            _foreignCheckboxState = Business.Address is ForeignAddress;
            if (_foreignCheckboxState)
            {
                AddressView = new CreateForeignAddressView();
                Business.Address = (AddressView.DataContext as ForeignAddressViewModel)!.Address;
            }
            else
            {
                AddressView = new CreateUSAddressView(_dataStore, taskFactory);
                Business.Address = (AddressView.DataContext as USAddressViewModel)!.Address;
            }
        }

        public async Task<bool> SaveAsync()
        {
            _ = await _dataStore.CreateAddressAsync(Business!.Address);
            await _dataStore.CreateBusinessAsync(Business);
            return false;
        }

        private void ForeignCheckboxChangeState(bool state)
        {
            if (state)
            {
                Business!.Address = new ForeignAddress();
                AddressView = new CreateForeignAddressView();
            }
            else
            {
                Business!.Address = new UsAddress();
                AddressView = new CreateUSAddressView(_dataStore, _taskFactory);
            }
        }
    }
}
