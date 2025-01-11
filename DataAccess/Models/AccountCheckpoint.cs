using System;

namespace DataAccess.Models
{
    public class AccountCheckpoint(Guid accountId, decimal balance)
    {
        public Guid AccountId { get; init; } = accountId;

        public decimal Balance { get; init; } = balance;
    }
}
