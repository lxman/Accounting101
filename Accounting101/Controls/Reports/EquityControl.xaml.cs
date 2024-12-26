using System.Windows.Controls;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Controls.Reports
{
    public partial class EquityControl : UserControl
    {
        public EquityControl()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetValues(IDataStore dataStore, JoinableTaskFactory taskFactory, List<AccountWithInfo> accts, DateOnly asOf)
        {
            AccountList.SetBalanceSheetValues(dataStore, taskFactory, accts, asOf);
        }

        public void ChangeDate(DateOnly date)
        {
            AccountList.ChangeBalanceSheetDate(date);
        }
    }
}