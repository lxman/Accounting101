using System.Windows.Controls;
using Accounting101.Views.Single;
using DataAccess.Models;

namespace Accounting101.Controls
{
    public partial class LedgerLineControl : UserControl
    {
        public DateOnly Date { get; }

        public decimal Balance { get; }

        public decimal Amount { get; }

        public CollapsibleAccountView OtherAccount { get; }

        public LedgerLineControl(Transaction t, decimal balance, AccountWithInfo otherAccount)
        {
            Date = DateOnly.FromDateTime(t.When);
            Balance = balance;
            Amount = t.Amount;
            OtherAccount = new CollapsibleAccountView(otherAccount, t.CreditedAccountId == otherAccount.Id);
            DataContext = this;
            InitializeComponent();
        }
    }
}