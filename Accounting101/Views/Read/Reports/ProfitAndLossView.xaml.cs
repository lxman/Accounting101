using System.Windows.Controls;
using Accounting101.ViewModels.Read.Reports;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Read.Reports
{
    public partial class ProfitAndLossView : UserControl
    {
        private readonly ProfitAndLossViewModel _viewModel = new();

        public ProfitAndLossView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            DataContext = _viewModel;
            InitializeComponent();
            List<AccountWithInfo> all = taskFactory.Run(() => dataStore.AccountsForClientAsync(clientId))?.ToList() ?? [];
            Revenue.SetInfo(dataStore, taskFactory, "Revenue", all.Where(a => a.Type == BaseAccountTypes.Revenue).ToList(), DateOnly.FromDateTime(DateTime.Today));
            Expenses.SetInfo(dataStore, taskFactory, "Expenses", all.Where(a => a.Type == BaseAccountTypes.Expense).ToList(), DateOnly.FromDateTime(DateTime.Today));
            Earnings.SetInfo(dataStore, taskFactory, "Earnings", all.Where(a => a.Type == BaseAccountTypes.Earnings).ToList(), DateOnly.FromDateTime(DateTime.Today));
        }
    }
}
