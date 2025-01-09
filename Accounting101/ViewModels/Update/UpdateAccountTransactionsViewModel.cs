using System.Collections.ObjectModel;
using Accounting101.Controls;
using Accounting101.Models;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace Accounting101.ViewModels.Update
{
    public class UpdateAccountTransactionsViewModel : BaseViewModel
    {
        public ReadOnlyObservableCollection<LedgerLineControl>? Transactions { get; private set; }

        private bool _isDebitAccount;

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, AccountWithTransactions account, List<AccountWithInfo> otherAccounts)
        {
            ObservableCollection<LedgerLineControl> lines = [];
            Transactions = null;
            Transactions = new ReadOnlyObservableCollection<LedgerLineControl>(lines);
            OnPropertyChanged(nameof(Transactions));

            if (account.Transactions.Count == 0)
            {
                return;
            }
            _isDebitAccount = account.IsDebitAccount;
            decimal balance = account.StartBalance;
            List<LedgerLineControl> ledgerLines = [];
            AccountWithInfo? otherAccount = null;
            account.Transactions.ForEach(t =>
            {
                bool wasCredited = t.CreditedAccountId == account.Id;
                if (t.CreditedAccountId == account.Id && !_isDebitAccount)
                {
                    balance += t.Amount;
                    otherAccount = otherAccounts.Find(a => a.Id == t.DebitedAccountId);
                }
                else if (_isDebitAccount)
                {
                    balance -= t.Amount;
                    otherAccount = otherAccounts.Find(a => a.Id == t.DebitedAccountId);
                }

                if (t.DebitedAccountId == account.Id && _isDebitAccount)
                {
                    balance += t.Amount;
                    otherAccount = otherAccounts.Find(a => a.Id == t.CreditedAccountId);
                }
                else if (!_isDebitAccount)
                {
                    balance -= t.Amount;
                    otherAccount = otherAccounts.Find(a => a.Id == t.CreditedAccountId);
                }
                string otherAccountInfo = otherAccount is null ? "Unknown" : $"{otherAccount.Info.CoAId} {otherAccount.Info.Name} {otherAccount.Type}";
                ledgerLines.Add(new LedgerLineControl(new TransactionInfoLine(t.Id, t.When, wasCredited ? t.Amount : null, !wasCredited ? t.Amount : null, balance, otherAccountInfo)));
            });
            Transactions = new ReadOnlyObservableCollection<LedgerLineControl>(new ObservableCollection<LedgerLineControl>(ledgerLines));
            OnPropertyChanged(nameof(Transactions));
        }
    }
}
