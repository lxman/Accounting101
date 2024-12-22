using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess;
using DataAccess.Interfaces;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
#pragma warning disable CS8618, CS9264

namespace Accounting101.Controls.Reports
{
    [ObservableObject]
    public partial class ClientInfoHeaderControl : UserControl
    {
        public string BusinessName
        {
            get => _businessName;
            set => SetProperty(ref _businessName, value);
        }

        public string Contact
        {
            get => _contact;
            set => SetProperty(ref _contact, value);
        }

        public string Address
        {
            get => _address;
            set => SetProperty(ref _address, value);
        }

        private string _businessName;
        private string _contact;
        private string _address;

        public ClientInfoHeaderControl()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetClient(IDataStore dataStore, JoinableTaskFactory taskFactory, Client c)
        {
            BusinessName = c.BusinessName;
            PersonName? contact = taskFactory.Run(() => dataStore.FindNameByIdAsync(c.PersonNameId));
            IAddress? address = taskFactory.Run(() => dataStore.FindAddressByIdAsync(c.AddressId));
            Contact = contact?.ToString() ?? string.Empty;
            Address = address?.ToString() ?? string.Empty;
        }
    }
}
