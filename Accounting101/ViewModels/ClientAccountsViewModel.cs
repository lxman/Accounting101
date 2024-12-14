using System.Windows;
using System.Windows.Controls;
using Accounting101.Models;
using Accounting101.Views.Single;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels
{
    public class ClientAccountsViewModel : BaseViewModel
    {
        public object AccountsList
        {
            get => _accountsList;
            set => SetField(ref _accountsList, value);
        }

        public ClientWithInfo Client { get; }

        public string Contact => Client.Name?.ToString() ?? string.Empty;

        public string Address => Client.Address?.ToString() ?? string.Empty;

        public List<AccountWithInfoFlat> Accounts { get; private set; }

        private object _accountsList;
        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;

        public ClientAccountsViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            Client = taskFactory.Run(() => dataStore.GetClientWithInfoAsync(clientId)) ?? throw new ArgumentException($"Client with id {clientId} not found.");
            Accounts = taskFactory.Run(() => dataStore.AccountsForClientAsync(clientId))?.Select(a => new AccountWithInfoFlat(dataStore, taskFactory, a)).ToList() ?? [];
            if (Accounts.Count == 0)
            {
                Grid createAccountGrid = new();
                createAccountGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                createAccountGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                createAccountGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Label noAccountsList = new() { Content = "No accounts found." };
                createAccountGrid.Children.Add(noAccountsList);
                Grid.SetRow(noAccountsList, 0);
                Button createAccountButton = new() { Content = "Create Account", Width = 150 };
                createAccountGrid.Children.Add(createAccountButton);
                Grid.SetRow(createAccountButton, 1);
                createAccountButton.Click += (sender, e) =>
                {
                    Account account = new() { ClientId = clientId, Type = BaseAccountTypes.Liability };
                    AccountInfo accountInfo = new() { CoAId = "100", Name = "Checking" };
                    taskFactory.Run(() => dataStore.CreateAccountAsync(new AccountWithInfo(account, accountInfo) { ClientId = clientId }));
                    Accounts = taskFactory.Run(() => dataStore.AccountsForClientAsync(clientId))?.Select(a => new AccountWithInfoFlat(dataStore, taskFactory, a)).ToList() ?? [];
                    AccountsList = new DataGrid { ItemsSource = Accounts };
                    ((DataGrid)AccountsList).SelectionChanged += SelectionChangedHandler;
                };
                Button createCoAButton = new() { Content = "Create Chart of Accounts", Width = 150 };
                createAccountGrid.Children.Add(createCoAButton);
                Grid.SetRow(createCoAButton, 2);
                createCoAButton.Click += (sender, e) =>
                {
                    taskFactory.Run(() => dataStore.CreateChartAsync(AvailableCoAs.SmallBusiness, Client));
                    Accounts = taskFactory.Run(() =>
                        dataStore.AccountsForClientAsync(clientId))?
                        .Select(a =>
                            new AccountWithInfoFlat(dataStore, taskFactory, a))
                        .OrderBy(a => a.CoAId).ToList() ?? [];
                    AccountsList = new DataGrid { ItemsSource = Accounts };
                    ((DataGrid)AccountsList).SelectionChanged += SelectionChangedHandler;
                };
                AccountsList = createAccountGrid;
            }
            else
            {
                Accounts = Accounts.OrderBy(a => a.CoAId).ToList();
                AccountsList = new DataGrid();
                ((DataGrid)AccountsList).SelectionChanged += SelectionChangedHandler;
                ((DataGrid)AccountsList).ItemsSource = Accounts;
            }
        }

        private void SelectionChangedHandler(object sender, SelectionChangedEventArgs e)
        {
            if (((DataGrid)sender).SelectedItem is not AccountWithInfoFlat account)
            {
                return;
            }
            AccountSelected(account);
        }

        private void AccountSelected(AccountWithInfoFlat accountWithInfoFlat)
        {
            AccountWithTransactions awt = new(_dataStore, accountWithInfoFlat.Id);
            AccountWithInfo awi = _taskFactory.Run(() => _dataStore.GetAccountWithInfoAsync(accountWithInfoFlat.Id))!;
            AccountsList = new AccountView(_dataStore, _taskFactory, awt, accountWithInfoFlat, awi);
        }
    }
}