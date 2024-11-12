using System;

namespace DataAccess.Models
{
    public class Account
    {
        public Guid Id { get; protected set; } = Guid.NewGuid();

        public BaseAccountingTypes Type { get; set; }

        public Guid ClientId { get; set; }

        public Guid InfoId { get; set; }

        public decimal StartBalance { get; set; }

        public bool IsDebitAccount => Type is BaseAccountingTypes.Asset or BaseAccountingTypes.Expense;

        public DateTime Created { get; set; } = DateTime.UtcNow;

        public Account()
        {
        }

        public Account(AccountWithInfo acct)
        {
            Id = acct.Id;
            Type = acct.Type;
            ClientId = acct.ClientId;
            InfoId = acct.InfoId;
            StartBalance = acct.StartBalance;
            Created = acct.Created;
        }
    }
}