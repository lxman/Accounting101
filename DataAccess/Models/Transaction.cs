using System;

// ReSharper disable ConvertToPrimaryConstructor

namespace DataAccess.Models
{
    public class Transaction
    {
        public Guid Id { get; set; }

        public Guid CreditAccountId { get; }

        public Guid DebitAccountId { get; }

        public decimal Amount { get; }

        public DateTime When { get; }

        public Transaction(Guid creditAccountId, Guid debitAccountId, decimal amount, DateTime when)
        {
            CreditAccountId = creditAccountId;
            DebitAccountId = debitAccountId;
            Amount = amount;
            When = when;
        }
    }
}