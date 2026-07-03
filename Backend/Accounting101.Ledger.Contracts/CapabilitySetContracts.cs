namespace Accounting101.Ledger.Contracts;

/// <summary>A capability set as returned by the admin API.</summary>
public sealed record CapabilitySetResponse(
    Guid Id, string Name, string? Description, IReadOnlyList<string> Capabilities, bool Builtin);

/// <summary>Create a new custom capability set. Every capability must be in the known vocabulary.</summary>
public sealed record CreateCapabilitySetRequest(
    string Name, string? Description, IReadOnlyList<string> Capabilities);

/// <summary>Replace an existing capability set's name/description/capabilities.</summary>
public sealed record UpdateCapabilitySetRequest(
    string Name, string? Description, IReadOnlyList<string> Capabilities);
