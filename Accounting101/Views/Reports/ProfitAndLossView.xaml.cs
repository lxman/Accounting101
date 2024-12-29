using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Reports
{
    [ObservableObject]
    public partial class ProfitAndLossView : UserControl
    {
        public DateTime StartDate
        {
            get => _startDate.ToDateTime(new TimeOnly());
            set
            {
                if (value > _endDate.ToDateTime(new TimeOnly()))
                {
                    _startDate = DateOnly.FromDateTime(_endDate.ToDateTime(new TimeOnly()));
                    ChangeDates(_startDate, _endDate);
                    return;
                }
                _startDate = DateOnly.FromDateTime(value);
                ChangeDates(_startDate, _endDate);
            }
        }

        public DateTime StartBeginDate { get; }

        public DateTime EndDate
        {
            get => _endDate.ToDateTime(new TimeOnly());
            set
            {
                if (value < _startDate.ToDateTime(new TimeOnly()))
                {
                    _endDate = DateOnly.FromDateTime(_startDate.ToDateTime(new TimeOnly()));
                    ChangeDates(_startDate, _endDate);
                    return;
                }
                _endDate = DateOnly.FromDateTime(value);
                ChangeDates(_startDate, _endDate);
            }
        }

        public DateTime EndBeginDate { get; }

        public decimal GrandSum
        {
            get => _grandSum;
            set => SetProperty(ref _grandSum, value);
        }

        private decimal _grandSum;
        private DateOnly _startDate = DateOnly.FromDateTime(DateTime.Now);
        private DateOnly _endDate = DateOnly.FromDateTime(DateTime.Now.AddDays(1));

        public ProfitAndLossView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            Business business = taskFactory.Run(dataStore.GetBusinessAsync)!;
            Client client = taskFactory.Run(() => dataStore.FindClientByIdAsync(clientId))!;
            List<AccountWithInfo>? accts = taskFactory.Run(() => dataStore.AccountsForClientAsync(clientId))?.ToList();
            if (accts is null)
            {
                throw new InvalidOperationException("No accounts found for client");
            }

            StartBeginDate = accts.Min(a => a.Created).ToDateTime(new TimeOnly());
            EndBeginDate = StartBeginDate.AddDays(1);

            List<AccountWithInfo> revenueAccounts = accts.Where(a => a.Type == BaseAccountTypes.Revenue).OrderBy(a => a.Info.CoAId).ToList();
            List<AccountWithInfo> expenseAccounts = accts.Where(a => a.Type == BaseAccountTypes.Expense).OrderBy(a => a.Info.CoAId).ToList();
            List<AccountWithInfo> earningsAccounts = accts.Where(a => a.Type == BaseAccountTypes.Earnings).OrderBy(a => a.Info.CoAId).ToList();

            DataContext = this;
            InitializeComponent();

            BusinessInfo.SetBusiness(business);
            ClientInfo.SetClient(dataStore, taskFactory, client);
            DateOnly currentDate = DateOnly.FromDateTime(DateTime.Today);
            RevenueAccounts.SetValues(dataStore, taskFactory, revenueAccounts, DateOnly.FromDateTime(StartBeginDate), DateOnly.FromDateTime(DateTime.Today));
            ExpenseAccounts.SetValues(dataStore, taskFactory, expenseAccounts, DateOnly.FromDateTime(StartBeginDate), DateOnly.FromDateTime(DateTime.Today));
            EarningsAccounts.SetValues(dataStore, taskFactory, earningsAccounts, DateOnly.FromDateTime(StartBeginDate), DateOnly.FromDateTime(DateTime.Today));
            StartDate = StartBeginDate;
            EndDate = DateTime.Today;
            OnPropertyChanged(nameof(StartBeginDate));
            OnPropertyChanged(nameof(EndBeginDate));
        }

        private void ChangeDates(DateOnly begin, DateOnly end)
        {
            RevenueAccounts.ChangeDate(begin, end);
            ExpenseAccounts.ChangeDate(begin, end);
            EarningsAccounts.ChangeDate(begin, end);
            GrandSum = RevenueAccounts.Sum + EarningsAccounts.Sum - ExpenseAccounts.Sum;
        }
    }
}