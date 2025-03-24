using System;
using Accounting101.Angular.DataAccess.Interfaces;

// ReSharper disable ConvertToPrimaryConstructor

namespace Accounting101.Angular.DataAccess.Models;

public class Transaction : IGlobalItem
{
    public Guid Id { get; set; }

    public Guid CreditedAccountId { get; }

    public Guid DebitedAccountId { get; }

    public decimal Amount { get; }

    public DateOnly When { get; }

    public Transaction() { }

    public Transaction(Guid creditedAccountId, Guid debitedAccountId, decimal amount, DateOnly when)
    {
        CreditedAccountId = creditedAccountId;
        DebitedAccountId = debitedAccountId;
        Amount = amount;
        When = when;
    }

    public Transaction(Guid id, Guid creditedAccountId, Guid debitedAccountId, decimal amount, DateOnly when)
    {
        Id = id;
        CreditedAccountId = creditedAccountId;
        DebitedAccountId = debitedAccountId;
        Amount = amount;
        When = when;
    }
}