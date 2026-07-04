namespace Accounting101.Ledger.Contracts;

/// <summary>Provision a new firm. <see cref="ClusterKey"/> defaults to the home cluster when omitted.</summary>
public sealed record ProvisionFirmRequest
{
    public required string Name { get; init; }
    public string? ClusterKey { get; init; }
}

/// <summary>A firm as returned by the platform control plane. Status is "Active" | "Suspended".</summary>
public sealed record FirmResponse(
    Guid Id, string Name, string Status, string ClusterKey, string ControlDatabase, DateTime CreatedUtc);

/// <summary>Set a firm's lifecycle status: "Active" | "Suspended".</summary>
public sealed record SetFirmStatusRequest(string Status);

/// <summary>Register a cluster the platform can place firms on.</summary>
public sealed record RegisterClusterRequest(string Key, string ConnectionString);

/// <summary>A registered cluster. The connection string is never returned — only whether one is set.</summary>
public sealed record ClusterResponse(string Key, bool HasConnectionString);

/// <summary>Per-firm usage snapshot the future billing subsystem consumes. No pricing logic here.</summary>
public sealed record UsageResponse(IReadOnlyList<FirmUsageResponse> Firms);

/// <summary>One firm's meter: active-client count and, per module key, how many active clients have it enabled.</summary>
public sealed record FirmUsageResponse(
    Guid FirmId, string Name, int ActiveClients, IReadOnlyDictionary<string, int> ModuleClientCounts);
