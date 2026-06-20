using Accounting101.Ledger.Core.Journal;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Mongo.Documents;

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

    /// <summary>
    /// Subledger dimensions as an array of (type, value) sub-documents — the indexable shape of the
    /// domain's type-keyed dictionary, so one ordinary multikey index over every dimension serves the
    /// subledger fold.
    /// </summary>
    public List<DimensionDocument> Dimensions { get; set; } = [];

    public string? LineMemo { get; set; }

    public static LineDocument FromDomain(Line l) => new()
    {
        Id = l.Id,
        AccountId = l.AccountId,
        Direction = l.Direction,
        Amount = l.Amount,
        Dimensions = l.Dimensions.Select(kv => new DimensionDocument { Type = kv.Key, Value = kv.Value }).ToList(),
        LineMemo = l.LineMemo,
    };

    public Line ToDomain() => new()
    {
        Id = Id,
        AccountId = AccountId,
        Direction = Direction,
        Amount = Amount,
        Dimensions = Dimensions.ToDictionary(d => d.Type, d => d.Value),
        LineMemo = LineMemo,
    };
}

/// <summary>One subledger tag on a line: the dimension type (e.g. "Customer") and the referenced entity id.</summary>
public sealed class DimensionDocument
{
    public string Type { get; set; } = string.Empty;
    public Guid Value { get; set; }
}
