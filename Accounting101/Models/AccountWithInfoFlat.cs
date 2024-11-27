using DataAccess.Models;

namespace Accounting101.Models
{
    public class AccountWithInfoFlat(AccountWithInfo accountWithInfo)
    {
        public string Name { get; } = accountWithInfo.Info.Name;

        public string CoAId { get; } = accountWithInfo.Info.CoAId;

        public decimal StartBalance { get; } = accountWithInfo.StartBalance;

        public DateTime Created { get; } = accountWithInfo.Created;

        public BaseAccountTypes Type { get; } = accountWithInfo.Type;

        public bool IsDebitAccount { get; } = accountWithInfo.IsDebitAccount;
    }
}
