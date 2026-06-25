namespace Accounting101.Invoicing;

/// <summary>Stored body of a payment — cash received from a customer, with its allocations across invoices.
/// Allocations may sum to less than Amount; the remainder becomes customer credit.</summary>
public sealed record PaymentBody(
    Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);

/// <summary>Stored body of a credit application — existing customer credit applied to invoices (no cash).</summary>
public sealed record CreditApplicationBody(
    Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations);
