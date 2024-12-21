using System.Windows.Controls;
using DataAccess.Models;

namespace Accounting101.Controls
{
    public partial class LedgerLineControl : UserControl
    {
        public Guid TransactionId { get; }

        public DateOnly Date { get; }

        public decimal? Debit { get; }

        public decimal? Credit { get; }

        public decimal Balance { get; }

        public string OtherAccount { get; }

        public AccountWithInfo OtherAccountInfo { get; }

        public Transaction Transaction { get; }

        public LedgerLineControl(Transaction t, decimal balance, AccountWithInfo otherAccount)
        {
            OtherAccountInfo = otherAccount;
            Transaction = t;
            TransactionId = t.Id;
            Date = DateOnly.FromDateTime(t.When);
            Balance = balance;
            if (t.CreditedAccountId == otherAccount.Id)
            {
                Debit = t.Amount;
                Credit = null;
            }
            else
            {
                Credit = t.Amount;
                Debit = null;
            }
            OtherAccount = $"{otherAccount.Info.CoAId} {otherAccount.Info.Name} {otherAccount.Type} {(t.CreditedAccountId == otherAccount.Id ? "Credited" : "Debited")}";
            DataContext = this;
            InitializeComponent();
        }
    }
}