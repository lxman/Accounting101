using System.Collections.ObjectModel;
using Accounting101.WPF.Models;
using DataAccess.WPF.Models;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace Accounting101.WPF.ViewModels.Update;

public class UpdateAccountTransactionsViewModel : BaseViewModel
{
    public ReadOnlyObservableCollection<TransactionInfoLine>? Transactions { get; private set; }

    public TransactionInfoLine? SelectedLine { private get; set; }

    private bool _isDebitAccount;

    public void SetInfo(AccountWithEverything account, List<AccountWithInfo> otherAccounts)
    {
        if (account.Transactions.Count == 0)
        {
            return;
        }
        _isDebitAccount = account.Account.IsDebitAccount;
        decimal balance = account.Account.StartBalance;
        List<TransactionInfoLine> ledgerLines = [];
        AccountWithInfo? otherAccount = null;
        account.Transactions.ForEach(t =>
        {
            bool wasCredited = t.CreditedAccountId == account.Account.Id;
            if (t.CreditedAccountId == account.Account.Id)
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
            else if (t.DebitedAccountId == account.Account.Id)
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
            ledgerLines.Add(
                new TransactionInfoLine(
                    t.Id,
                    t.When,
                    wasCredited
                        ? t.Amount
                        : null,
                    !wasCredited
                        ? t.Amount
                        : null,
                    balance,
                    otherAccountInfo,
                    account.CheckPoint is null || (account.CheckPoint is not null && t.When > account.CheckPoint.Date)));
        });
        Transactions = null;
        Transactions = new ReadOnlyObservableCollection<TransactionInfoLine>(new ObservableCollection<TransactionInfoLine>(ledgerLines.OrderBy(l => l.When)));
        OnPropertyChanged(nameof(Transactions));
    }

    public TransactionInfoLine? GetSelectedLine()
    {
        return SelectedLine;
    }
}