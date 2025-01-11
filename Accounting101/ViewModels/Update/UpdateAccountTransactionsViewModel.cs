using System.Collections.ObjectModel;
using Accounting101.Models;
using DataAccess.Models;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace Accounting101.ViewModels.Update
{
    public class UpdateAccountTransactionsViewModel : BaseViewModel
    {
        public ReadOnlyObservableCollection<TransactionInfoLine>? Transactions { get; private set; }

        public TransactionInfoLine? SelectedLine { private get; set; }

        private bool _isDebitAccount;

        public void SetInfo(AccountWithTransactions account, List<AccountWithInfo> otherAccounts)
        {
            if (account.Transactions.Count == 0)
            {
                return;
            }
            _isDebitAccount = account.IsDebitAccount;
            decimal balance = account.StartBalance;
            List<TransactionInfoLine> ledgerLines = [];
            AccountWithInfo? otherAccount = null;
            account.Transactions.ForEach(t =>
            {
                bool wasCredited = t.CreditedAccountId == account.Id;
                if (t.CreditedAccountId == account.Id)
                {
                    if (!_isDebitAccount)
                    {
                        balance += t.Amount;
                    }
                    else
                    {
                        balance -= t.Amount;
                    }
                    otherAccount = otherAccounts.Find(a => a.Id == t.DebitedAccountId);
                }
                else if (t.DebitedAccountId == account.Id)
                {
                    if (_isDebitAccount)
                    {
                        balance += t.Amount;
                    }
                    else
                    {
                        balance -= t.Amount;
                    }
                    otherAccount = otherAccounts.Find(a => a.Id == t.CreditedAccountId);
                }
                string otherAccountInfo = otherAccount is null ? "Unknown" : $"{otherAccount.Info.CoAId} {otherAccount.Info.Name} {otherAccount.Type}";
                ledgerLines.Add(new TransactionInfoLine(t.Id, t.When, wasCredited ? t.Amount : null, !wasCredited ? t.Amount : null, balance, otherAccountInfo));
            });
            Transactions = null;
            Transactions = new ReadOnlyObservableCollection<TransactionInfoLine>(new ObservableCollection<TransactionInfoLine>(ledgerLines));
            OnPropertyChanged(nameof(Transactions));
        }

        public TransactionInfoLine? GetSelectedLine()
        {
            return SelectedLine;
        }
    }
}