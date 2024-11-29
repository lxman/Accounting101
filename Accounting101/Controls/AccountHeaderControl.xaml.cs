using System.Windows.Controls;
using Accounting101.Models;

namespace Accounting101.Controls
{
    public partial class AccountHeaderControl : UserControl
    {
        public decimal CurrentBalance { get; }

        public string CoAId { get; }

        public DateTime Created { get; }

        public string DebitCredit { get; }

        public new string Name { get; }

        public decimal StartBalance { get; }

        public BaseAccountTypes Type { get; }

        public AccountHeaderControl(AccountWithInfoFlat a)
        {
            DataContext = this;
            InitializeComponent();
            CurrentBalance = a.Balance;
            CoAId = a.CoAId;
            Created = a.Created;
            DebitCredit = a.IsDebitAccount ? "Debit Account" : "Credit Account";
            Name = a.Name;
            StartBalance = a.StartBalance;
            Type = a.Type;
        }
    }
}