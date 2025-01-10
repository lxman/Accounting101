using System.Windows.Controls;
using Accounting101.Models;

namespace Accounting101.Controls
{
    public partial class LedgerLineControl : UserControl
    {
        public Guid Id => _source.Id;

        public string When => _source.When.ToString();

        public decimal? Credit => _source.Credit;

        public decimal? Debit => _source.Debit;

        public decimal Balance => _source.Balance;

        public string OtherAccountInfo => _source.OtherAccountInfo;

        private readonly TransactionInfoLine _source;

        public LedgerLineControl(TransactionInfoLine source)
        {
            _source = source;
            DataContext = this;
            InitializeComponent();
        }

        public LedgerLineLayout GetLayout()
        {
            return new LedgerLineLayout
            {
                DateWidth = DateWidth.ActualWidth,
                CreditWidth = CreditWidth.ActualWidth,
                DebitWidth = DebitWidth.ActualWidth,
                BalanceWidth = BalanceWidth.ActualWidth
            };
        }

        public void PerformLayout(LedgerLineLayout layout)
        {
            DateWidth.MinWidth = layout.DateWidth;
            DateWidth.MaxWidth = layout.DateWidth;
            CreditWidth.MinWidth = layout.CreditWidth;
            CreditWidth.MaxWidth = layout.CreditWidth;
            DebitWidth.MinWidth = layout.DebitWidth;
            DebitWidth.MaxWidth = layout.DebitWidth;
            BalanceWidth.MinWidth = layout.BalanceWidth;
            BalanceWidth.MaxWidth = layout.BalanceWidth;
        }
    }
}