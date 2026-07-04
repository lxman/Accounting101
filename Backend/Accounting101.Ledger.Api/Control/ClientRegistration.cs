using MongoDB.Bson;
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

    /// <summary>
    /// When true, the host enforces segregation of duties: an entry must be approved by someone other
    /// than its author. Off by default — some shops (e.g. a sole proprietor) allow self-approval. This
    /// policy lives here in the control DB, not in the engine.
    /// </summary>
    public bool RequireSegregationOfDuties { get; set; }

    /// <summary>The month (1-12) the client's fiscal year ends; the fiscal year ends on the LAST day of
    /// that month. December (12) by default — a per-client policy, like SoD. Legacy registrations stored
    /// before this field existed deserialize to 0; readers normalize via <see cref="FiscalYear.MonthOf"/>.</summary>
    public int FiscalYearEndMonth { get; set; } = 12;

    /// <summary>Lifecycle state. Defaults to <see cref="ClientStatus.Active"/>; a missing field on a legacy
    /// document also deserializes to Active. Archiving stops the meter but keeps the ledger DB.</summary>
    [BsonRepresentation(BsonType.String)]
    public ClientStatus Status { get; set; } = ClientStatus.Active;

    /// <summary>The module keys this client is entitled to (e.g. "receivables", "payables", "payroll").
    /// Doubles as the billing meter (per-module fee) and the access gate (Phase 3 checks it at the module
    /// authorization chokepoint). Empty by default; a missing field on a legacy document deserializes to
    /// empty.</summary>
    public IReadOnlyList<string> EnabledModules { get; set; } = [];

    /// <summary>When the client was provisioned (UTC). Legacy documents deserialize to default(DateTime).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When the client was archived (UTC), or null while active.</summary>
    public DateTime? ArchivedUtc { get; set; }
}
