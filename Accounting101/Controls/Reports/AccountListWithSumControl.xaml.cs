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

        public void SetBalanceSheetValues(IDataStore dataStore, JoinableTaskFactory taskFactory, List<AccountWithInfo> accts, DateOnly asOf)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _accounts = accts;
            PopulateBalanceSheet(asOf);
        }

        public void ChangeBalanceSheetDate(DateOnly date)
        {
            Accounts.Clear();
            PopulateBalanceSheet(date);
        }

        public void SetProfitLossValues(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            List<AccountWithInfo> accts,
            DateOnly begin,
            DateOnly end)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _accounts = accts;
        }

        public void ChangeProfitLossDates(DateOnly begin, DateOnly end)
        {
            Accounts.Clear();
            PopulateProfitLoss(begin, end);
        }

        private void PopulateBalanceSheet(DateOnly date)
        {
            _accounts.ForEach(a =>
            {
                Accounts.Add(new AccountWithBalanceControl(a, _taskFactory.Run(() => _dataStore.GetAccountBalanceOnDateAsync(a.Id, date))));
            });
            OnPropertyChanged(nameof(Sum));
        }

        private void PopulateProfitLoss(DateOnly begin, DateOnly end)
        {
            _accounts.ForEach(a =>
            {
                decimal beginBalance = _taskFactory.Run(() => _dataStore.GetAccountBalanceOnDateAsync(a.Id, begin));
                decimal endBalance = _taskFactory.Run(() => _dataStore.GetAccountBalanceOnDateAsync(a.Id, end));
                Accounts.Add(new AccountWithBalanceControl(a, endBalance - beginBalance));
            });
            OnPropertyChanged(nameof(Sum));
        }
    }
}
