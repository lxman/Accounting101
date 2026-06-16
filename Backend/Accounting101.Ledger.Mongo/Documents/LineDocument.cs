using Accounting101.Ledger.Core.Journal;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Mongo storage shape for a <see cref="Line"/>. Amount is stored as Decimal128
/// (never floating point); Direction is stored as a string for readability and
/// resilience to enum reordering.
/// </summary>
public sealed class LineDocument
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public Direction Direction { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Amount { get; set; }

    public Guid? CustomerId { get; set; }
    public Guid? VendorId { get; set; }
    public Guid? ItemId { get; set; }
    public string? LineMemo { get; set; }

    public static LineDocument FromDomain(Line l) => new()
    {
        Id = l.Id,
        AccountId = l.AccountId,
        Direction = l.Direction,
        Amount = l.Amount,
        CustomerId = l.CustomerId,
        VendorId = l.VendorId,
        ItemId = l.ItemId,
        LineMemo = l.LineMemo,
    };

    public Line ToDomain() => new()
    {
        Id = Id,
        AccountId = AccountId,
        Direction = Direction,
        Amount = Amount,
        CustomerId = CustomerId,
        VendorId = VendorId,
        ItemId = ItemId,
        LineMemo = LineMemo,
    };
}
