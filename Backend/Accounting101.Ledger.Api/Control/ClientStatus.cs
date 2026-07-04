namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// Lifecycle of a client's books. <see cref="Active"/> is the billable/usable state; <see cref="Archived"/>
/// stops the per-client meter while the ledger DB is retained intact (accounting data must survive for
/// years, so closing a client is never a delete). <see cref="Active"/> is 0 so legacy documents written
/// before this field existed deserialize to Active.
/// </summary>
public enum ClientStatus
{
    Active = 0,
    Archived = 1,
}
