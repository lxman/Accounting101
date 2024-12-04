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
            CreditAccount = taskFactory.Run(() => dataStore.GetAccountWithInfoAsync(t.CreditedAccountId))
                            ?? throw new ArgumentException($"Account with id {t.CreditedAccountId} not found.");
            DebitAccount = taskFactory.Run(() => dataStore.GetAccountWithInfoAsync(t.DebitedAccountId))
                            ?? throw new ArgumentException($"Account with id {t.DebitedAccountId} not found.");
            if (CreditAccount.Id == relativeAccountId)
            {
                Account1 = new CollapsibleAccountView(CreditAccount, t.CreditedAccountId == CreditAccount.Id);
                Account2 = new CollapsibleAccountView(DebitAccount, t.CreditedAccountId == DebitAccount.Id);
            }
            else
            {
                Account1 = new CollapsibleAccountView(DebitAccount, t.CreditedAccountId == DebitAccount.Id);
                Account2 = new CollapsibleAccountView(CreditAccount, t.CreditedAccountId == CreditAccount.Id);
            }
        }
    }
}