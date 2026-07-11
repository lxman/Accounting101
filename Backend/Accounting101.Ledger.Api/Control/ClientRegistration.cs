using Accounting101.Ledger.Contracts;
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
    /// LEGACY. Superseded by <see cref="ApprovalMode"/>; retained only so documents written before the
    /// enum existed still deserialize. Never written going forward — <c>ApprovalPolicy.ModeOf</c> reads it
    /// only when <see cref="ApprovalMode"/> is <see cref="Contracts.ApprovalMode.Unspecified"/>.
    /// </summary>
    public bool RequireSegregationOfDuties { get; set; }

    /// <summary>The client's approval posture (two-person / self-approve / auto-approve). Host policy, stored
    /// here in the control DB, not in the engine. A legacy document with no value deserializes to
    /// <see cref="Contracts.ApprovalMode.Unspecified"/>; read the effective mode via <c>ApprovalPolicy.ModeOf</c>.</summary>
    [BsonRepresentation(BsonType.String)]
    public ApprovalMode ApprovalMode { get; set; }

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
