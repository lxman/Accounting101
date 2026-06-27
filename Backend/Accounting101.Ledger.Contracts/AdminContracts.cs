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

/// <summary>Grant a user a role on a client. Role: Auditor | Clerk | Approver | Controller | Admin.</summary>
public sealed record AddMemberRequest(Guid UserId, string Role);

public sealed record MembershipResponse(Guid UserId, Guid ClientId, string Role);
