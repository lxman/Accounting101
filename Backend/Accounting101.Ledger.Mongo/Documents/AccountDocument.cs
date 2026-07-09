using Accounting101.Ledger.Core.Accounts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo.Documents;

/// <summary>Mongo storage shape for a chart-of-accounts <see cref="Account"/>. Enums stored as strings.
/// Tolerates unknown/legacy elements (matches the sibling Control docs) so a chart written before a field
/// rename remains readable.</summary>
[BsonIgnoreExtraElements]
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

    public List<string> RequiredDimensions { get; set; } = [];

    /// <summary>Legacy single-dimension element from before the RequiredDimension → RequiredDimensions
    /// rename. Mapped read-only migration input — <see cref="ToDomain"/> seeds
    /// <see cref="RequiredDimensions"/> from it when a document predates the set. Never written back out
    /// (<see cref="FromDomain"/> leaves it null; new/updated docs carry only <see cref="RequiredDimensions"/>).</summary>
    [BsonElement("RequiredDimension")]
    public string? LegacyRequiredDimension { get; set; }

    [BsonRepresentation(BsonType.String)]
    public CashFlowActivity? CashFlowActivity { get; set; }

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
        RequiredDimensions = a.RequiredDimensions.ToList(),
        CashFlowActivity = a.CashFlowActivity,
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
        RequiredDimensions = RequiredDimensions is { Count: > 0 }
            ? RequiredDimensions
            : LegacyRequiredDimension is { } legacy ? [legacy] : [],
        CashFlowActivity = CashFlowActivity,
        IsRetainedEarnings = IsRetainedEarnings,
        Active = Active,
    };
}
