using System.Windows;
using Accounting101.WPF.Messages;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

// ReSharper disable NotAccessedField.Local
#pragma warning disable CS8629 // Nullable value type may be null.
#pragma warning disable CS8601 // Possible null reference assignment.
#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.Views.Read;

public partial class ClientWithAccountListView : IRecipient<ShowAccountTransactionEditor>
{
    private readonly IDataStore _dataStore;
    private readonly JoinableTaskFactory _taskFactory;
    private readonly ClientWithInfo _client;
    private Guid? _accountId;
    private AccountWithInfo _account;

    public ClientWithAccountListView(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client)
    {
        CheckPoint? checkPoint = null;
        if (client.CheckPointId is not null)
        {
            checkPoint = taskFactory.Run(() => dataStore.GetCheckpointAsync(client.Id));
        }
        WeakReferenceMessenger.Default.Register(this);
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        _client = client;
        InitializeComponent();
        ClientHeader.SetInfo(client, checkPoint);
        AccountsGrid.SetInfo(dataStore, taskFactory, client);
        AccountEntriesEditor.IsVisibleChanged += AccountEntriesEditorVisibleChanged;
        CreateCoAView.CoACreated += CreateCoAViewCoACreated;
        if (!AccountsGrid.HasAccounts)
        {
            AccountsGrid.Visibility = Visibility.Hidden;
            AccountEntriesEditor.Visibility = Visibility.Hidden;
            CreateCoAView.Visibility = Visibility.Visible;
            EditAccount.Visibility = Visibility.Hidden;
            CreateCoAView.SetInfo(dataStore, taskFactory, client);
        }
        else
        {
            AccountsGrid.Visibility = Visibility.Visible;
            AccountEntriesEditor.Visibility = Visibility.Hidden;
            CreateCoAView.Visibility = Visibility.Hidden;
            EditAccount.Visibility = Visibility.Hidden;
        }
    }

    public void Receive(ShowAccountTransactionEditor message)
    {
        if (!message.Value.Value)
        {
            return;
        }

        _accountId = message.Value.AccountId;
        AccountWithEverything? accountWithEverything =
            _taskFactory.Run(() => _dataStore.GetAccountWithEverythingAsync(_accountId.Value));
        if (accountWithEverything is null)
        {
            return;
        }
        AccountEntriesEditor.SetInfo(_dataStore, _taskFactory, _client, accountWithEverything);
        AccountEntriesEditor.Visibility = Visibility.Visible;
        AccountsGrid.Visibility = Visibility.Hidden;
        CreateCoAView.Visibility = Visibility.Hidden;
    }

    public void UpdateAccountInfo(AccountWithInfo account)
    {
        _accountId = account.Id;
        AccountsGrid.Visibility = Visibility.Hidden;
        EditAccount.Visibility = Visibility.Visible;
        AccountEntriesEditor.Visibility = Visibility.Hidden;
        EditAccount.SetAccount(account);
        EditAccount.SaveChanges += EditAccountSaveChanges;
    }

    public void SaveAccountChanges()
    {
        EditAccount.SaveAccountChanges();
    }

    private void EditAccountSaveChanges(object? sender, AccountWithInfo? e)
    {
        if (e is null)
        {
            return;
        }
        _taskFactory.Run(() => _dataStore.UpdateAccountAsync(e));
        EditAccount.Visibility = Visibility.Hidden;
        AccountEntriesEditor.Visibility = Visibility.Visible;
    }

    private void CreateCoAViewCoACreated(object? sender, EventArgs e)
    {
        CreateCoAView.Visibility = Visibility.Hidden;
        AccountsGrid.SetInfo(_dataStore, _taskFactory, _client);
        AccountsGrid.Visibility = Visibility.Visible;
    }

    private void AccountEntriesEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (EditAccount.Visibility != Visibility.Visible)
        {
            _accountId = null;
            WeakReferenceMessenger.Default.Send(
                new SetEditAccountVisibleMessage(_accountId));
            return;
        }

        WeakReferenceMessenger.Default.Send(
            new SetEditAccountVisibleMessage(_accountId));
        _account = _taskFactory.Run(() => _dataStore.GetAccountWithInfoAsync(_accountId.Value));
    }
}