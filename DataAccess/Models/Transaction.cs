using System;
// ReSharper disable ConvertToPrimaryConstructor

namespace DataAccess.Models
{
    public class Transaction
    {
        public Guid Id { get; set; }

        public Guid CreditAccountId { get; }

        public Guid DebitAccountIds { get; }

        public decimal Amount { get; }

        public DateTime When { get; }

        public Transaction(Guid creditAccountId, Guid debitAccountIds, decimal amount, DateTime when)
        {
            CreditAccountId = creditAccountId;
            DebitAccountIds = debitAccountIds;
            Amount = amount;
            When = when;
        }
    }
}