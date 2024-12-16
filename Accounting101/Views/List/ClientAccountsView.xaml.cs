using System.Windows.Controls;
using System.Windows.Input;
using Accounting101.ViewModels.List;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.List
{
    public partial class ClientAccountsView : UserControl
    {
        private readonly ClientAccountsViewModel _vm;

        public ClientAccountsView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            ClientAccountsViewModel vm = new(dataStore, taskFactory, clientId);
            _vm = vm;
            DataContext = vm;
            InitializeComponent();
        }

        private void ClientMouseDown(object sender, MouseButtonEventArgs e)
        {
            _vm.SwitchToClientList();
        }
    }
}