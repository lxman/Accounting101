using Accounting101.Ledger.Mongo;
using Accounting101.Ledger.Mongo.Reporting;

namespace Accounting101.Ledger.Api.Tenancy;

/// <summary>
/// The ledger stack bound to one client's database: the coordinating <see cref="LedgerService"/>
/// for writes, plus the individual stores for reads and the <see cref="FinancialStatementService"/>
/// for derived statements. Built per request by <see cref="ClientLedgerFactory"/>.
/// </summary>
public sealed record ClientLedger(
    LedgerService Service,
    MongoJournalStore Journal,
    MongoAuditLog Audit,
    MongoBalanceProjection Projection,
    MongoCheckpointStore Checkpoints,
    MongoAccountStore Accounts,
    FinancialStatementService Statements);
