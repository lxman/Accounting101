using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Control;

/// <summary>Approval-posture helper. Resolves the effective <see cref="ApprovalMode"/> for a client,
/// falling back to the legacy <see cref="ClientRegistration.RequireSegregationOfDuties"/> bool for
/// documents written before the enum existed (lazy migration — no backfill). Mirrors
/// <see cref="FiscalYear.MonthOf"/>.</summary>
public static class ApprovalPolicy
{
    public static ApprovalMode ModeOf(ClientRegistration client) =>
        client.ApprovalMode != ApprovalMode.Unspecified
            ? client.ApprovalMode
            : client.RequireSegregationOfDuties ? ApprovalMode.TwoPerson : ApprovalMode.SelfApprove;
}
