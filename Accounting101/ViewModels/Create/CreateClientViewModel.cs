using System.Windows.Controls;
using Accounting101.Models;
using Accounting101.Views.Create;
using DataAccess.Models;

namespace Accounting101.ViewModels.Create
{
    public class CreateClientViewModel : BaseViewModel
    {
        public string BusinessName { get; set; } = string.Empty;

        public PersonName PersonName { private get; set; } = new();

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

        private bool? _foreign = false;
        private UserControl? _addressView;
        private readonly CreateUSAddressView _usAddressView = new();
        private readonly CreateForeignAddressView _foreignAddressView = new();

        public CreateClientViewModel(List<string> states)
        {
            _usAddressView.SetStates(states);
            AddressView = _usAddressView;
        }

        public ClientInfo GetClientInfo()
        {
            return new ClientInfo
            {
                BusinessName = BusinessName,
                PersonName = PersonName,
                Address = AddressView switch
                {
                    CreateUSAddressView => _usAddressView.GetResult(),
                    CreateForeignAddressView => _foreignAddressView.GetResult(),
                    _ => null
                }
            };
        }

        private void ForeignChanged()
        {
            AddressView = Foreign == true ? _foreignAddressView : _usAddressView;
        }
    }
}