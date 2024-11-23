using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using Accounting101.Views.Create;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels
{
    public class CreateBusinessViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

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

        public Business Business { get; set; }

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
                AddressView = new CreateForeignAddressView();
            }
            else
            {
                AddressView = new CreateUSAddressView(_dataStore);
            }
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

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
