namespace Accounting101.Ledger.Mongo;

/// <summary>The kind of change recorded in the audit log.</summary>
public enum AuditAction
{
    Created,
    Approved,
    Voided,
    Superseded,
    PeriodClosed,
}
