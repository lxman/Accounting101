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
    }
}
