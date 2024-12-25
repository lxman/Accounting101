using System.Windows.Controls;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Controls.Reports
{
    public partial class RevenueControl : UserControl
    {
        public RevenueControl()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetValues(IDataStore dataStore, JoinableTaskFactory taskFactory, List<AccountWithInfo> accts, DateOnly begin, DateOnly end)
        {
            AccountList.SetProfitLossValues(dataStore, taskFactory, accts, begin, end);
        }

        public void ChangeDate(DateOnly begin, DateOnly end)
        {
            AccountList.ChangeProfitLossDates(begin, end);
        }
    }
}
