namespace Accounting101.Ledger.Contracts;

/// <summary>Provision a new client (a set of books). The ledger database name is generated if omitted.</summary>
public sealed record CreateClientRequest
{
    public required string Name { get; init; }
    public string? DatabaseName { get; init; }
    /// <summary>Approval posture. Defaults to TwoPerson when omitted (see the create handler).</summary>
    public ApprovalMode ApprovalMode { get; init; }
    /// <summary>Month (1-12) the fiscal year ends; defaults to December.</summary>
    public int FiscalYearEndMonth { get; init; } = 12;
}

public sealed record ClientRegistrationResponse(
    Guid Id, string Name, string DatabaseName, ApprovalMode ApprovalMode, int FiscalYearEndMonth);

/// <summary>Change a client's fiscal-year-end month (1-12), forward-only. Already-closed years are
/// immutable; this affects only future closes.</summary>
public sealed record SetFiscalYearEndRequest(int FiscalYearEndMonth);

/// <summary>A client's current fiscal-year-end month (1-12).</summary>
public sealed record FiscalYearEndResponse(int FiscalYearEndMonth);

/// <summary>Grant a user a role on a client. Role: Auditor | Clerk | Approver | Controller | Admin.</summary>
public sealed record AddMemberRequest(Guid UserId, string Role);

public sealed record MembershipResponse(
    Guid UserId, Guid ClientId, IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities,
    IReadOnlyList<Guid>? GrantedSetIds = null, IReadOnlyList<string>? SetNames = null);

/// <summary>The acting user's resolved capabilities on a client, the role preset(s) granted,
/// whether they hold the deployment-admin claim (a separate authorization axis), and the client's
/// enabled module keys (drives frontend nav filtering).</summary>
public sealed record CapabilitiesResponse(
    IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin,
    IReadOnlyList<string> EnabledModules);

/// <summary>Add a member to a client with an explicit role preset list and capability set.</summary>
public sealed record AddClientMemberRequest(Guid UserId, IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities);

/// <summary>Replace an existing member's role presets and capability set.</summary>
public sealed record SetMemberRequest(IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities);

/// <summary>Assign a member to one or more capability sets (the go-forward, live-bound grant).
/// Resolved capabilities are the union of the referenced sets' current capabilities — never client-supplied.</summary>
public sealed record AssignSetsRequest(IReadOnlyList<Guid> SetIds);

/// <summary>The full capability vocabulary and the role presets (backend truth for the admin editor).</summary>
public sealed record CapabilityCatalogResponse(IReadOnlyList<string> Capabilities, IReadOnlyList<RolePresetDto> Roles);
public sealed record RolePresetDto(string Role, IReadOnlyList<string> Capabilities);

/// <summary>One control-plane audit entry as returned by GET /admin/audit.</summary>
public sealed record AdminAuditEntryResponse(
    Guid Id, DateTime Timestamp, Guid ActorUserId, bool ActorIsDeploymentAdmin, string Action,
    Guid? ClientId, Guid? TargetUserId, Guid? TargetSetId,
    IReadOnlyList<string>? BeforeCapabilities, IReadOnlyList<string>? AfterCapabilities);

/// <summary>Replace a client's entitled module keys (default-closed access + billing meter). Idempotent.</summary>
public sealed record SetClientModulesRequest(IReadOnlyList<string> ModuleKeys);

/// <summary>A client's entitled module keys as returned by the entitlement setter.</summary>
public sealed record ClientModulesResponse(Guid ClientId, IReadOnlyList<string> ModuleKeys);

/// <summary>A client's current approval mode.</summary>
public sealed record ApprovalPolicyResponse(ApprovalMode Mode);

/// <summary>Change a client's approval mode. <see cref="ApprovalMode.Unspecified"/> is rejected (422).</summary>
public sealed record SetApprovalPolicyRequest(ApprovalMode Mode);
