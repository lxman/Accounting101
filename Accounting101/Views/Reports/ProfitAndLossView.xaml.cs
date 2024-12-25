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
                _startDate = DateOnly.FromDateTime(value);
            }
        }

        public DateTime StartBeginDate { get; }

        public DateTime EndDate
        {
            get => _endDate.ToDateTime(new TimeOnly());
            set
            {
                _endDate = DateOnly.FromDateTime(value);
            }
        }

        public DateTime EndBeginDate { get; }

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

            List<AccountWithInfo> revenueAccounts = accts.Where(a => a.Type == BaseAccountTypes.Revenue).OrderBy(a => a.Info.CoAId).ToList();
            List<AccountWithInfo> expenseAccounts = accts.Where(a => a.Type == BaseAccountTypes.Expense).OrderBy(a => a.Info.CoAId).ToList();
            List<AccountWithInfo> earningsAccounts = accts.Where(a => a.Type == BaseAccountTypes.Earnings).OrderBy(a => a.Info.CoAId).ToList();

            DataContext = this;
            InitializeComponent();

            BusinessInfo.SetBusiness(business);
            ClientInfo.SetClient(dataStore, taskFactory, client);
            DateOnly currentDate = DateOnly.FromDateTime(DateTime.Today);
        }
    }
}
