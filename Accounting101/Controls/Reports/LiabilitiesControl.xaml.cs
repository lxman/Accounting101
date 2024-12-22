using System.Windows.Controls;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Controls.Reports
{
    public partial class LiabilitiesControl : UserControl
    {
        public LiabilitiesControl()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetValues(IDataStore dataStore, JoinableTaskFactory taskFactory, List<AccountWithInfo> accts)
        {
            AccountList.SetValues(dataStore, taskFactory, accts);
        }
    }
}
