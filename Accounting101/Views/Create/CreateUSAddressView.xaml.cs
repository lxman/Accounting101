using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;

namespace Accounting101.Views.Create
{
    public partial class CreateUSAddressView : UserControl
    {
        public CreateUSAddressView(IDataStore dataStore)
        {
            DataContext = new USAddressViewModel(dataStore);
            InitializeComponent();
        }
    }
}
