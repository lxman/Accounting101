using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.List
{
    public partial class ClientAccountsView : UserControl
    {
        public ClientAccountsView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            DataContext = new ClientAccountsViewModel(dataStore, taskFactory, clientId);
            InitializeComponent();
        }
    }
}