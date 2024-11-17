using System.Windows.Controls;
using Accounting101.ViewModels;
using Accounting101.Views.Single;
using DataAccess.Services.Interfaces;

namespace Accounting101.Views.List
{
    /// <summary>
    /// Interaction logic for ClientListView.xaml
    /// </summary>
    public partial class ClientListView : UserControl
    {
        public ClientListView(IDataStore dataStore)
        {
            InitializeComponent();
            ClientListViewModel viewModel = new(dataStore);
            DataContext = viewModel;
            viewModel.Clients.ToList().ForEach(c =>
            {
                ClientList.Children.Add(new ClientView(dataStore, c.Id));
            });
        }
    }
}
