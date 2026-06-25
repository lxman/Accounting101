namespace Accounting101.Payables;

/// <summary>A vendor — the payables module's reference entity (mirrors the invoicing Customer).</summary>
public sealed record Vendor
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Email { get; init; }
}
