namespace Accounting101.Receivables;

/// <summary>
/// A customer the receivables module owns. Its <see cref="Id"/> is what rides on the A/R line as the
/// "Customer" dimension, so the engine's A/R-by-customer subledger resolves back to this record.
/// </summary>
public sealed record Customer
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Email { get; init; }
}
