using System.Windows.Controls;
using Accounting101.Models;
using Accounting101.ViewModels.Update;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
#pragma warning disable VSTHRD110

namespace Accounting101.Views.Update
{
    public partial class UpdateAccountTransactionsView : UserControl
    {
        private readonly UpdateAccountTransactionsViewModel _viewModel = new();

        public UpdateAccountTransactionsView()
        {
            DataContext = _viewModel;
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, AccountWithTransactions account, List<AccountWithInfo> otherAccounts)
        {
            _viewModel.SetInfo(dataStore, taskFactory, account, otherAccounts);
        }

        public void PerformLayout()
        {
            List<LedgerLineLayout> layouts = _viewModel.GetLayouts();

            layouts.Add(new LedgerLineLayout()
            {
                DateWidth = Date.ActualWidth,
                CreditWidth = Credit.ActualWidth,
                DebitWidth = Debit.ActualWidth,
                BalanceWidth = Balance.ActualWidth
            });
            LedgerLineLayout common = new()
            {
                DateWidth = layouts.Max(l => l.DateWidth),
                CreditWidth = layouts.Max(l => l.CreditWidth),
                DebitWidth = layouts.Max(l => l.DebitWidth),
                BalanceWidth = layouts.Max(l => l.BalanceWidth)
            };
            Dispatcher.BeginInvoke(() =>
            {
                Date.MinWidth = common.DateWidth;
                Date.MaxWidth = common.DateWidth;
                Credit.MinWidth = common.CreditWidth;
                Credit.MaxWidth = common.CreditWidth;
                Debit.MinWidth = common.DebitWidth;
                Debit.MaxWidth = common.DebitWidth;
                Balance.MinWidth = common.BalanceWidth;
                Balance.MaxWidth = common.BalanceWidth;
                _viewModel.PerformLayout(common);
            });
        }
    }
}
