using System;
// ReSharper disable ConvertToPrimaryConstructor

namespace DataAccess.Models
{
    public class Transaction
    {
        public Guid Id { get; set; }

        public Guid CreditAccount { get; }

        public Guid DebitAccount { get; }

        public decimal Amount { get; }

        public DateTime When { get; }

        public Transaction(Guid creditAccount, Guid debitAccount, decimal amount, DateTime when)
        {
            CreditAccount = creditAccount;
            DebitAccount = debitAccount;
            Amount = amount;
            When = when;
        }
    }
}