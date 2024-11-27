using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;

namespace Accounting101.Views.List
{
    /// <summary>
    /// Interaction logic for CoAView.xaml
    /// </summary>
    public partial class ClientAccountsView : UserControl
    {
        public ClientAccountsView(IDataStore dataStore, Guid clientId)
        {
            DataContext = new ClientAccountsViewModel(dataStore, clientId);
            InitializeComponent();
        }
    }
}
