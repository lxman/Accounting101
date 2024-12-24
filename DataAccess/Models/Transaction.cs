using System;

// ReSharper disable ConvertToPrimaryConstructor

namespace DataAccess.Models
{
    public class Transaction
    {
        public Guid Id { get; set; }

        public Guid CreditedAccountId { get; }

        public Guid DebitedAccountId { get; }

        public decimal Amount { get; }

        public DateOnly When { get; }

        public Transaction(Guid creditedAccountId, Guid debitedAccountId, decimal amount, DateOnly when)
        {
            CreditedAccountId = creditedAccountId;
            DebitedAccountId = debitedAccountId;
            Amount = amount;
            When = when;
        }
    }
}