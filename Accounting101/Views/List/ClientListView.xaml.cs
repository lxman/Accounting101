using System.Windows;
using System.Windows.Controls;
using Accounting101.ViewModels;
using Accounting101.Views.Single;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.List
{
    /// <summary>
    /// Interaction logic for ClientListView.xaml
    /// </summary>
    public partial class ClientListView : UserControl
    {
        public event EventHandler<Guid>? ClientChosen;

        public ClientListView(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            ClientListViewModel viewModel = new(dataStore, taskFactory);
            DataContext = viewModel;
            InitializeComponent();
            viewModel.Clients.ToList().ForEach(c =>
            {
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
