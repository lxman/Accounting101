using System.Windows.Controls;
using Accounting101.ViewModels;
using Accounting101.Views.Single;
using DataAccess.Services.Interfaces;

namespace Accounting101.Views.List
{
    /// <summary>
    /// Interaction logic for ClientListControl.xaml
    /// </summary>
    public partial class ClientListControl : UserControl
    {
        public ClientListControl(IDataStore dataStore)
        {
            InitializeComponent();
            ClientListControlViewModel viewModel = new(dataStore);
            DataContext = viewModel;
            viewModel.Clients.ToList().ForEach(c =>
            {
                ClientList.Children.Add(new ClientControl(dataStore, c.Id));
            });
        }
    }
}
