using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Reports
{
    [ObservableObject]
    public partial class BalanceSheetView : UserControl
    {
        public DateTime Date
        {
            get => _date.ToDateTime(new TimeOnly());
            set
            {
                _date = DateOnly.FromDateTime(value);
                ChangeDate(_date);
            }
        }

        public DateTime BeginDate { get; }

        private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);

        public BalanceSheetView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            Business business = taskFactory.Run(dataStore.GetBusinessAsync)!;
            Client client = taskFactory.Run(() => dataStore.FindClientByIdAsync(clientId))!;
            List<AccountWithInfo>? accts = taskFactory.Run(() => dataStore.AccountsForClientAsync(clientId))?.ToList();
            if (accts is null)
            {
                throw new InvalidOperationException("No accounts found for client");
            }

            BeginDate = accts.Min(a => a.Created).ToDateTime(new TimeOnly());

            List<AccountWithInfo> assetAccounts = accts.Where(a => a.Type == BaseAccountTypes.Asset).OrderBy(a => a.Info.CoAId).ToList();
            List<AccountWithInfo> liabilityAccounts = accts.Where(a => a.Type == BaseAccountTypes.Liability).OrderBy(a => a.Info.CoAId).ToList();
            List<AccountWithInfo> equityAccounts = accts.Where(a => a.Type == BaseAccountTypes.Equity).OrderBy(a => a.Info.CoAId).ToList();
            DataContext = this;

            InitializeComponent();

            BusinessInfo.SetBusiness(business);
            ClientInfo.SetClient(dataStore, taskFactory, client);
            DateOnly currentDate = DateOnly.FromDateTime(DateTime.Today);
            AssetAccounts.SetValues(dataStore, taskFactory, assetAccounts, currentDate);
            LiabilityAccounts.SetValues(dataStore, taskFactory, liabilityAccounts, currentDate);
            EquityAccounts.SetValues(dataStore, taskFactory, equityAccounts, currentDate);
            Date = DateTime.Today;
            OnPropertyChanged(nameof(BeginDate));
        }

        private void ChangeDate(DateOnly date)
        {
            AssetAccounts.ChangeDate(date);
            LiabilityAccounts.ChangeDate(date);
            EquityAccounts.ChangeDate(date);
        }
    }
}
