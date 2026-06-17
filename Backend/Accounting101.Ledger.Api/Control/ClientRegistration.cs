using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// A client (one set of books) registered in the deployment's control database. Maps the stable
/// client id to the MongoDB database that holds that client's ledger. The control DB is firm-level
/// (one per deployment); the client databases it points at are the isolated ledgers.
/// </summary>
public sealed class ClientRegistration
{
    [BsonId]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The MongoDB database name holding this client's ledger (journal, balances, audit, …).</summary>
    public string DatabaseName { get; set; } = string.Empty;
}
