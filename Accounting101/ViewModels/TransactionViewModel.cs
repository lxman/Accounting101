using Accounting101.Views.Single;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels
{
    public class TransactionViewModel : BaseViewModel
    {
        public CollapsibleAccountView Account1 { get; }

        public CollapsibleAccountView Account2 { get; }

        public Transaction Transaction { get; }

        public AccountWithInfo CreditAccount { get; }

        public AccountWithInfo DebitAccount { get; }

        public TransactionViewModel(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            Transaction t,
            Guid relativeAccountId)
        {
            Transaction = t;
            CreditAccount = taskFactory.Run(() => dataStore.GetAccountWithInfoAsync(t.CreditAccountId))
                            ?? throw new ArgumentException($"Account with id {t.CreditAccountId} not found.");
            DebitAccount = taskFactory.Run(() => dataStore.GetAccountWithInfoAsync(t.DebitAccountId))
                            ?? throw new ArgumentException($"Account with id {t.DebitAccountId} not found.");
            if (CreditAccount.Id == relativeAccountId)
            {
                Account1 = new CollapsibleAccountView(CreditAccount, !CreditAccount.IsDebitAccount);
                Account2 = new CollapsibleAccountView(DebitAccount, !DebitAccount.IsDebitAccount);
            }
            else
            {
                Account1 = new CollapsibleAccountView(DebitAccount, !DebitAccount.IsDebitAccount);
                Account2 = new CollapsibleAccountView(CreditAccount, !CreditAccount.IsDebitAccount);
            }
        }
    }
}