using System.Windows.Controls;
using Accounting101.Models;

namespace Accounting101.Controls
{
    public partial class AccountHeaderControl : UserControl
    {
        public decimal CurrentBalance { get; }

        public string CoAId { get; }

        public DateOnly Created { get; }

        public string DebitCredit { get; }

        public string AccountName { get; }

        public decimal StartBalance { get; }

        public BaseAccountTypes Type { get; }

        public AccountHeaderControl(AccountWithInfoFlat a)
        {
            CurrentBalance = a.Balance;
            CoAId = a.CoAId;
            Created = DateOnly.FromDateTime(a.Created);
            DebitCredit = a.IsDebitAccount ? "Debit Account" : "Credit Account";
            AccountName = a.Name;
            StartBalance = a.StartBalance;
            Type = a.Type;
            DataContext = this;
            InitializeComponent();
        }
    }
}