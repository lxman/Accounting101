namespace Accounting101.Ledger.Api.Contracts;

/// <summary>Provision a new client (a set of books). The ledger database name is generated if omitted.</summary>
public sealed record CreateClientRequest
{
    public required string Name { get; init; }
    public string? DatabaseName { get; init; }
    public bool RequireSegregationOfDuties { get; init; }
}

public sealed record ClientRegistrationResponse(Guid Id, string Name, string DatabaseName, bool RequireSegregationOfDuties);

/// <summary>Grant a user a role on a client. Role: Auditor | Clerk | Approver | Controller | Admin.</summary>
public sealed record AddMemberRequest(Guid UserId, string Role);

public sealed record MembershipResponse(Guid UserId, Guid ClientId, string Role);
