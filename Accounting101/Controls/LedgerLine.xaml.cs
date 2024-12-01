using System.Windows.Controls;
using Accounting101.Views.Single;
using DataAccess.Models;

namespace Accounting101.Controls
{
    /// <summary>
    /// Interaction logic for LedgerLine.xaml
    /// </summary>
    public partial class LedgerLine : UserControl
    {
        public DateOnly Date { get; }

        public decimal Balance { get; }

        public decimal Amount { get; }

        public CollapsibleAccountView OtherAccount { get; }

        public LedgerLine(AccountWithInfo a, Transaction t, decimal balance)
        {
            Date = DateOnly.FromDateTime(t.When);
            Balance = balance;
            Amount = t.Amount;
            OtherAccount = new CollapsibleAccountView(a);
            DataContext = this;
            InitializeComponent();
        }
    }
}
