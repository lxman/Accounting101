using System.Windows.Controls;
using DataAccess.Models;

namespace Accounting101.Controls.Reports
{
    public partial class AccountWithBalanceControl : UserControl
    {
        public string CoAId { get; }

        public string AccountName { get; }

        public decimal Balance { get; }

        public AccountWithBalanceControl(AccountWithInfo acct, decimal balance)
        {
            CoAId = acct.Info.CoAId;
            AccountName = acct.Info.Name;
            Balance = balance;
            DataContext = this;
            InitializeComponent();
        }
    }
}
