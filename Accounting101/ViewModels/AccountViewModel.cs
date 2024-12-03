using System.Collections.ObjectModel;
using Accounting101.Controls;
using Accounting101.Models;
using DataAccess.Models;

namespace Accounting101.ViewModels
{
    public class AccountViewModel : BaseViewModel
    {
        public AccountHeaderControl AccountHeaderControl { get; }

        public ObservableCollection<LedgerLineControl> Transactions { get; }

        public AccountViewModel(AccountWithTransactions f, AccountWithInfoFlat a, AccountWithInfo awi)
        {
            AccountHeaderControl = new AccountHeaderControl(a);
            decimal balance = a.StartBalance;
            List<LedgerLineControl> lines = [];
            f.Transactions.ForEach(t =>
            {
                if (t.CreditAccountId == a.Id)
                {
                    if (a.IsDebitAccount) balance -= t.Amount;
                    else balance += t.Amount;
                }
                else
                {
                    if (a.IsDebitAccount) balance += t.Amount;
                    else balance -= t.Amount;
                }
                lines.Add(new LedgerLineControl(awi, t, balance));
            });
            Transactions = new ObservableCollection<LedgerLineControl>(lines);
        }
    }
}