using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Models
{
    public class AccountWithInfoFlat(IDataStore dataStore, JoinableTaskFactory taskFactory, AccountWithInfo accountWithInfo)
    {
        public Guid Id = accountWithInfo.Id;

        public string Name { get; } = accountWithInfo.Info.Name;

        public string CoAId { get; } = accountWithInfo.Info.CoAId;

        public decimal StartBalance { get; } = accountWithInfo.StartBalance;

        public DateTime Created { get; } = accountWithInfo.Created;

        public BaseAccountTypes Type { get; } = accountWithInfo.Type;

        public bool IsDebitAccount { get; } = accountWithInfo.IsDebitAccount;

        public decimal Balance => taskFactory.Run(() => dataStore.GetAccountBalanceAsync(accountWithInfo.Id));
    }
}