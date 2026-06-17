using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// Grants a user access to a client's books. Membership is the firm-level authorization grouping:
/// the host reads it to answer "may this user touch client X?" before resolving the client's
/// database. Authentication (who the user is) is upstream; this is authorization (what they reach).
/// </summary>
public sealed class Membership
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public Guid ClientId { get; set; }
}
