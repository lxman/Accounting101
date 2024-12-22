using System.Windows.Controls;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Reports
{
    public partial class BalanceSheetView : UserControl
    {
        public string AsOfDate { get; }

        public BalanceSheetView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            Business business = taskFactory.Run(dataStore.GetBusinessAsync)!;
            Client client = taskFactory.Run(() => dataStore.FindClientByIdAsync(clientId))!;
            List<AccountWithInfo>? accts = taskFactory.Run(() => dataStore.AccountsForClientAsync(clientId))?.ToList();
            if (accts is null)
            {
                throw new InvalidOperationException("No accounts found for client");
            }

            List<AccountWithInfo> assetAccounts = accts.Where(a => a.Type == BaseAccountTypes.Asset).OrderBy(a => a.Info.CoAId).ToList();
            List<AccountWithInfo> liabilityAccounts = accts.Where(a => a.Type == BaseAccountTypes.Liability).OrderBy(a => a.Info.CoAId).ToList();
            List<AccountWithInfo> equityAccounts = accts.Where(a => a.Type == BaseAccountTypes.Equity).OrderBy(a => a.Info.CoAId).ToList();
            AsOfDate = $"As Of {DateTime.Now:MM/dd/yyyy}";
            DataContext = this;
            InitializeComponent();
            BusinessInfo.SetBusiness(business);
            ClientInfo.SetClient(dataStore, taskFactory, client);
            AssetAccounts.SetValues(dataStore, taskFactory, assetAccounts);
            LiabilityAccounts.SetValues(dataStore, taskFactory, liabilityAccounts);
            EquityAccounts.SetValues(dataStore, taskFactory, equityAccounts);
        }
    }
}
