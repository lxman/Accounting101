using System.Collections.ObjectModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Controls.Reports
{
    [INotifyPropertyChanged]
    public partial class AccountListWithSumControl : UserControl
    {
        public ObservableCollection<AccountWithBalanceControl> Accounts { get; } = [];

        public decimal Sum => Accounts.Sum(a => a.Balance);

        public AccountListWithSumControl()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetValues(IDataStore dataStore, JoinableTaskFactory taskFactory, List<AccountWithInfo> accts)
        {
            accts.ForEach(a =>
            {
                Accounts.Add(new AccountWithBalanceControl(a, taskFactory.Run(() => dataStore.GetAccountBalanceAsync(a.Id))));
            });
            OnPropertyChanged(nameof(Sum));
        }
    }
}
