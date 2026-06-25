namespace Accounting101.Receivables;

/// <summary>The stored shape of a customer — the opaque body the engine persists. The customer id is the
/// document id, so it is not repeated here. Distinct from the domain <see cref="Customer"/> so the body
/// round-trips cleanly through the document store.</summary>
public sealed record CustomerBody(string Name, string? Email);
