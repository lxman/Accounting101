using System.Windows;
using System.Windows.Controls;
using Accounting101.ViewModels.List;
using Accounting101.Views.Single;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.List
{
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
                ClientList.Children.Add(new ClientView(dataStore, taskFactory, c.Id));
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