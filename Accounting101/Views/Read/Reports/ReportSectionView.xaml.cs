using System.Collections.ObjectModel;
using System.Windows.Controls;
using Accounting101.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
#pragma warning disable CS8618, CS9264

namespace Accounting101.Views.Read.Reports
{
    [ObservableObject]
    public partial class ReportSectionView : UserControl
    {
        public string SectionHeader { get; private set; }

        public ReadOnlyObservableCollection<AccountsViewLine> Accounts { get; private set; }

        public DateOnly Date
        {
            get => _date;
            set
            {
                _date = value;
                OnPropertyChanged();
                CalculateBalances();
            }
        }

        public decimal Total
        {
            get => _total;
            set
            {
                _total = value;
                OnPropertyChanged();
            }
        }

        private decimal _total;
        private DateOnly _date;
        private IDataStore _dataStore;
        private JoinableTaskFactory _taskFactory;
        private List<AccountWithInfo> _accounts;

        public ReportSectionView()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, string sectionHeader, List<AccountWithInfo> accounts, DateOnly date)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _accounts = accounts;
            SectionHeader = sectionHeader;
            OnPropertyChanged(nameof(SectionHeader));
            Date = date;
        }

        private void CalculateBalances()
        {
            List<AccountsViewLine> accounts = [];
            _total = 0;
            _accounts.ForEach(a =>
            {
                decimal balance = _taskFactory.Run(() => _dataStore.GetAccountBalanceOnDateAsync(a.Id, Date));
                _total += balance;
                accounts.Add(new AccountsViewLine
                {
                    CoAId = a.Info.CoAId,
                    Created = a.Created,
                    CurrentBalance = balance,
                    Id = a.Id,
                    Name = a.Info.Name,
                    Type = a.Type,
                    StartBalance = a.StartBalance
                });
            });
            Accounts = new ReadOnlyObservableCollection<AccountsViewLine>(
                new ObservableCollection<AccountsViewLine>(accounts));
            OnPropertyChanged(nameof(Accounts));
            OnPropertyChanged(nameof(Total));
        }
    }
}
