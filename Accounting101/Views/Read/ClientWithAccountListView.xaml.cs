using System.Windows;
using System.Windows.Controls;
using Accounting101.Messages;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Read
{
    public partial class ClientWithAccountListView : UserControl, IRecipient<ShowAccountTransactionEditor>
    {
        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;
        private readonly ClientWithInfo _client;
        private Guid? _accountId;

        public ClientWithAccountListView(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client)
        {
            WeakReferenceMessenger.Default.Register(this);
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _client = client;
            InitializeComponent();
            ClientHeader.SetInfo(client);
            AccountsGrid.SetInfo(dataStore, taskFactory, client);
            AccountEntriesEditor.IsVisibleChanged += AccountEntriesEditorVisibleChanged;
            CreateCoAView.CoACreated += CreateCoAViewCoACreated;
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

        private void CreateCoAViewCoACreated(object? sender, EventArgs e)
        {
            CreateCoAView.Visibility = Visibility.Hidden;
            AccountsGrid.SetInfo(_dataStore, _taskFactory, _client);
            AccountsGrid.Visibility = Visibility.Visible;
        }

        private void AccountEntriesEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(bool)e.NewValue)
            {
                _accountId = null;
            }
            WeakReferenceMessenger.Default.Send(
                new SetEditAccountVisibleMessage(_accountId));
        }

        public void Receive(ShowAccountTransactionEditor message)
        {
            if (!message.Value.Value)
            {
                return;
            }

            _accountId = message.Value.AccountId;
            AccountWithTransactions awt = new(_dataStore, _taskFactory, message.Value.AccountId);
            AccountEntriesEditor.SetInfo(_dataStore, _taskFactory, _client, awt);
            AccountEntriesEditor.Visibility = Visibility.Visible;
            AccountsGrid.Visibility = Visibility.Hidden;
            CreateCoAView.Visibility = Visibility.Hidden;
        }
    }
}
