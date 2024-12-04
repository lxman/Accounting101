using System.Collections.ObjectModel;
using Accounting101.Controls;
using Accounting101.Models;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels
{
    public class AccountViewModel : BaseViewModel
    {
        public AccountHeaderControl AccountHeaderControl { get; }

        public ObservableCollection<LedgerLineControl> Transactions { get; }

        public AccountViewModel(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            AccountWithTransactions f,
            AccountWithInfoFlat a,
            AccountWithInfo awi)
        {
            AccountHeaderControl = new AccountHeaderControl(a);
            decimal balance = a.StartBalance;
            List<LedgerLineControl> lines = [];
            f.Transactions.ForEach(t =>
            {
                Guid otherAccountId = t.DebitedAccountId == a.Id ? t.CreditedAccountId : t.DebitedAccountId;
                AccountWithInfo otherAccount = taskFactory.Run(() => dataStore.GetAccountWithInfoAsync(otherAccountId))!;
                if (t.CreditedAccountId == a.Id)
                {
                    if (a.IsDebitAccount) balance -= t.Amount;
                    else balance += t.Amount;
                }
                else
                {
                    if (a.IsDebitAccount) balance += t.Amount;
                    else balance -= t.Amount;
                }
                lines.Add(new LedgerLineControl(t, balance, otherAccount));
            });
            Transactions = new ObservableCollection<LedgerLineControl>(lines);
        }
    }
}