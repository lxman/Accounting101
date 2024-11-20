using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;

namespace Accounting101.Views.Single
{
    /// <summary>
    /// Interaction logic for USAddressView.xaml
    /// </summary>
    public partial class USAddressView : UserControl
    {
        public USAddressView(IDataStore dataStore, Guid id)
        {
            InitializeComponent();
            DataContext = new USAddressViewModel(dataStore, id);
        }
    }
}
