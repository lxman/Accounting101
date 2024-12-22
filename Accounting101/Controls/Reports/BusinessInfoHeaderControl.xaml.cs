using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess.Models;
#pragma warning disable CS8618, CS9264

namespace Accounting101.Controls.Reports
{
    [ObservableObject]
    public partial class BusinessInfoHeaderControl : UserControl
    {
        public string BusinessName
        {
            get => _businessName;
            set => SetProperty(ref _businessName, value);
        }

        public string Address
        {
            get => _address;
            set => SetProperty(ref _address, value);
        }

        private string _businessName;
        private string _address;

        public BusinessInfoHeaderControl()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetBusiness(Business business)
        {
            BusinessName = business.Name;
            Address = business.Address.ToString();
        }
    }
}
