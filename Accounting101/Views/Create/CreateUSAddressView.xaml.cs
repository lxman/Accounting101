using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;

namespace Accounting101.Views.Create
{
    public partial class CreateUSAddressView : UserControl
    {
        public CreateUSAddressView(IDataStore dataStore)
        {
            InitializeComponent();
            StateSelector.ComboItems = dataStore.GetStates().Order().Cast<object>().ToList();
            DataContext = new CreateUSAddressViewModel();
        }
    }
}
