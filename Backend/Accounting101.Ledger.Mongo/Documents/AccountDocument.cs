using Accounting101.Ledger.Core.Accounts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo.Documents;

/// <summary>Mongo storage shape for a chart-of-accounts <see cref="Account"/>. Enums stored as strings.</summary>
public sealed class AccountDocument
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public string Number { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public AccountType Type { get; set; }

    public Guid? ParentId { get; set; }
    public bool Postable { get; set; } = true;

    [BsonRepresentation(BsonType.String)]
    public DimensionKind? RequiredDimension { get; set; }

    public bool IsRetainedEarnings { get; set; }
    public bool Active { get; set; } = true;

    public static AccountDocument FromDomain(Account a) => new()
    {
        Id = a.Id,
        ClientId = a.ClientId,
        Number = a.Number,
        Name = a.Name,
        Type = a.Type,
        ParentId = a.ParentId,
        Postable = a.Postable,
        RequiredDimension = a.RequiredDimension,
        IsRetainedEarnings = a.IsRetainedEarnings,
        Active = a.Active,
    };

    public Account ToDomain() => new()
    {
        Id = Id,
        ClientId = ClientId,
        Number = Number,
        Name = Name,
        Type = Type,
        ParentId = ParentId,
        Postable = Postable,
        RequiredDimension = RequiredDimension,
        IsRetainedEarnings = IsRetainedEarnings,
        Active = Active,
    };
}
