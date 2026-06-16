namespace Accounting101.Ledger.Mongo;

/// <summary>
/// The authenticated principal performing an operation, handed to the engine by the host.
/// The engine neither authenticates nor authorizes — it records this snapshot in the audit
/// log. Claims are opaque (host-defined); the engine stores them, it does not interpret them.
/// </summary>
public sealed record Actor
{
    public required Guid UserId { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<Claim> Claims { get; init; } = [];
}

/// <summary>An opaque, host-defined claim (e.g. role=approver), recorded verbatim for audit.</summary>
public sealed record Claim(string Type, string Value);
