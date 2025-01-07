using System.Windows;
using System.Windows.Controls;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Read
{
    public partial class ClientWithAccountListView : UserControl
    {
        public ClientWithAccountListView(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client)
        {
            InitializeComponent();
            ClientHeader.SetInfo(client);
            AccountsGrid.SetInfo(dataStore, taskFactory, client);
            if (!AccountsGrid.HasAccounts)
            {
                AccountsGrid.Visibility = Visibility.Hidden;
                AccountEntriesEditor.Visibility = Visibility.Hidden;
                CreateCoAView.Visibility = Visibility.Visible;
                CreateCoAView.SetInfo(dataStore, taskFactory, client);
            }
            else
            {
                AccountsGrid.Visibility = Visibility.Visible;
                AccountEntriesEditor.Visibility = Visibility.Hidden;
                CreateCoAView.Visibility = Visibility.Hidden;
            }
        }
    }
}
