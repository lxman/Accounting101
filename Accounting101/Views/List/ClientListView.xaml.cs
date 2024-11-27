using System.Windows;
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
        public event EventHandler<Guid>? ClientChosen;

        public ClientListView(IDataStore dataStore)
        {
            ClientListViewModel viewModel = new(dataStore);
            DataContext = viewModel;
            InitializeComponent();
            viewModel.Clients.ToList().ForEach(c =>
            {
                ClientView cv = new(dataStore, c.Id);
                ClientList.Children.Add(new ClientView(dataStore, c.Id));
            });
            foreach (UIElement element in ClientList.Children)
            {
                ClientView cv = (ClientView)element;
                cv.ClientChosen += ClientClicked;
            }
        }

        private void ClientClicked(object? sender, Guid id)
        {
            ClientChosen?.Invoke(sender, id);
        }
    }
}
