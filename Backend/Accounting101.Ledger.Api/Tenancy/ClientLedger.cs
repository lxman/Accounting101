using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Tenancy;

/// <summary>
/// The ledger stack bound to one client's database: the coordinating <see cref="LedgerService"/>
/// for writes, plus the individual stores for reads. Built per request by
/// <see cref="ClientLedgerFactory"/>.
/// </summary>
public sealed record ClientLedger(
    LedgerService Service,
    MongoJournalStore Journal,
    MongoAuditLog Audit,
    MongoBalanceProjection Projection,
    MongoCheckpointStore Checkpoints);
