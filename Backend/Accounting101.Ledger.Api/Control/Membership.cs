using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// Grants a user a role on a client's books. Membership is the firm-level authorization grouping:
/// the host reads it to answer "may this user touch client X, and to do what?" before resolving the
/// client's database. Authentication (who the user is) is upstream; this is authorization.
/// </summary>
public sealed class Membership
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public Guid ClientId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public LedgerRole Role { get; set; }
}
