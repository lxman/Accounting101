using System.Windows.Controls;
using Accounting101.ViewModels.Read.Reports;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Read.Reports
{
    public partial class BalanceSheetView : UserControl
    {
        private readonly BalanceSheetViewModel _viewModel = new();

        public BalanceSheetView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            DataContext = _viewModel;
            InitializeComponent();
            List<AccountWithInfo> all = taskFactory.Run(() => dataStore.AccountsForClientAsync(clientId))?.ToList() ?? [];
            Assets.SetInfo(dataStore, taskFactory, "Assets", all.Where(a => a.Type == BaseAccountTypes.Asset).ToList(), DateOnly.FromDateTime(DateTime.Today));
            Liabilities.SetInfo(dataStore, taskFactory, "Liabilities", all.Where(a => a.Type == BaseAccountTypes.Liability).ToList(), DateOnly.FromDateTime(DateTime.Today));
            Equity.SetInfo(dataStore, taskFactory, "Equity", all.Where(a => a.Type == BaseAccountTypes.Equity).ToList(), DateOnly.FromDateTime(DateTime.Today));
        }
    }
}
