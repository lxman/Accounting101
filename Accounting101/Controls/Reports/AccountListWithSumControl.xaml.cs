using System.Collections.ObjectModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
#pragma warning disable CS8618, CS9264

namespace Accounting101.Controls.Reports
{
    [INotifyPropertyChanged]
    public partial class AccountListWithSumControl : UserControl
    {
        public ObservableCollection<AccountWithBalanceControl> Accounts { get; } = [];

        public decimal Sum => Accounts.Sum(a => a.Balance);

        private IDataStore _dataStore;
        private JoinableTaskFactory _taskFactory;
        private List<AccountWithInfo> _accounts;

        public AccountListWithSumControl()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetValues(IDataStore dataStore, JoinableTaskFactory taskFactory, List<AccountWithInfo> accts, DateOnly asOf)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _accounts = accts;
            accts.ForEach(a =>
            {
                Accounts.Add(new AccountWithBalanceControl(a, taskFactory.Run(() => dataStore.GetAccountBalanceOnDateAsync(a.Id, asOf))));
            });
            OnPropertyChanged(nameof(Sum));
        }

        public void ChangeDate(DateOnly date)
        {
            Accounts.Clear();
            _accounts.ForEach(a =>
            {
                Accounts.Add(new AccountWithBalanceControl(a, _taskFactory.Run(() => _dataStore.GetAccountBalanceOnDateAsync(a.Id, date))));
            });
            OnPropertyChanged(nameof(Sum));
        }
    }
}
