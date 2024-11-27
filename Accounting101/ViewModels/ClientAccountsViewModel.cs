using System.Windows;
using System.Windows.Controls;
using Accounting101.Models;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
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

        public ClientAccountsViewModel(IDataStore dataStore, Guid clientId)
        {
            Client = dataStore.GetClientWithInfo(clientId) ?? throw new ArgumentException($"Client with id {clientId} not found.");
            Accounts = dataStore.AccountsForClient(clientId)?.Select(a => new AccountWithInfoFlat(a)).ToList() ?? [];
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
                    dataStore.CreateAccount(new AccountWithInfo(account, accountInfo) { ClientId = clientId });
                    Accounts = dataStore.AccountsForClient(clientId)?.Select(a => new AccountWithInfoFlat(a)).ToList() ?? [];
                    AccountsList = new DataGrid { ItemsSource = Accounts };
                };
                Button createCoAButton = new() { Content = "Create Chart of Accounts", Width = 150 };
                createAccountGrid.Children.Add(createCoAButton);
                Grid.SetRow(createCoAButton, 2);
                createCoAButton.Click += (sender, e) =>
                {
                    dataStore.CreateChart(AvailableCoAs.SmallBusiness, Client);
                    Accounts = dataStore.AccountsForClient(clientId)?.Select(a => new AccountWithInfoFlat(a)).ToList() ?? [];
                    AccountsList = new DataGrid { ItemsSource = Accounts };
                };
                AccountsList = createAccountGrid;
            }
            else
            {
                Accounts = Accounts.OrderBy(a => a.CoAId).ToList();
                AccountsList = new DataGrid();
                ((DataGrid)AccountsList).ItemsSource = Accounts;
            }
        }
    }
}
