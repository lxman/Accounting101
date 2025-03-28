using System;
using Accounting101.Angular.DataAccess.Interfaces;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

// ReSharper disable ConvertToPrimaryConstructor

namespace Accounting101.Angular.DataAccess.Models;

public class Transaction : IGlobalItem
{
    public Guid Id { get; set; }

    [BsonRepresentation(BsonType.String)]
    public string CreditedAccountId { get; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public string DebitedAccountId { get; } = string.Empty;

    [BsonRepresentation(BsonType.Decimal128, AllowOverflow = true, AllowTruncation = true)]
    public decimal Amount { get; }

    [BsonDateOnlyOptions(BsonType.DateTime, DateOnlyDocumentFormat.YearMonthDay)]
    public DateOnly When { get; }

    public Transaction() { }

    public Transaction(string creditedAccountId, string debitedAccountId, decimal amount, DateOnly when)
    {
        CreditedAccountId = creditedAccountId;
        DebitedAccountId = debitedAccountId;
        Amount = amount;
        When = when;
    }

    public Transaction(Guid id, string creditedAccountId, string debitedAccountId, decimal amount, DateOnly when)
    {
        Id = id;
        CreditedAccountId = creditedAccountId;
        DebitedAccountId = debitedAccountId;
        Amount = amount;
        When = when;
    }
}