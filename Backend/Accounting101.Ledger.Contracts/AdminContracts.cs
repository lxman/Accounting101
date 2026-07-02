namespace Accounting101.Ledger.Contracts;

/// <summary>Provision a new client (a set of books). The ledger database name is generated if omitted.</summary>
public sealed record CreateClientRequest
{
    public required string Name { get; init; }
    public string? DatabaseName { get; init; }
    public bool RequireSegregationOfDuties { get; init; }
    /// <summary>Month (1-12) the fiscal year ends; defaults to December.</summary>
    public int FiscalYearEndMonth { get; init; } = 12;
}

public sealed record ClientRegistrationResponse(
    Guid Id, string Name, string DatabaseName, bool RequireSegregationOfDuties, int FiscalYearEndMonth);

/// <summary>Change a client's fiscal-year-end month (1-12), forward-only. Already-closed years are
/// immutable; this affects only future closes.</summary>
public sealed record SetFiscalYearEndRequest(int FiscalYearEndMonth);

/// <summary>Grant a user a role on a client. Role: Auditor | Clerk | Approver | Controller | Admin.</summary>
public sealed record AddMemberRequest(Guid UserId, string Role);

public sealed record MembershipResponse(
    Guid UserId, Guid ClientId, IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities);
