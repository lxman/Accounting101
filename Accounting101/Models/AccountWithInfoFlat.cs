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

        public string StartBalance { get; } = accountWithInfo.StartBalance.ToString("#,##0.00;(#,##0.00);0");

        public DateOnly Created { get; } = accountWithInfo.Created;

        public BaseAccountTypes Type { get; } = accountWithInfo.Type;

        public bool IsDebitAccount { get; } = accountWithInfo.IsDebitAccount;

        public string Balance => taskFactory.Run(() => dataStore.GetAccountBalanceAsync(accountWithInfo.Id)).ToString("#,##0.00;(#,##0.00);0");

        public decimal GetStartBalance()
        {
            return accountWithInfo.StartBalance;
        }

        public decimal GetBalance()
        {
            return Convert.ToDecimal(Balance.Replace(",", string.Empty).Replace("(", string.Empty).Replace(")", string.Empty));
        }
    }
}