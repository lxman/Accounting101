using Accounting101.Payables;
using Accounting101.Settlement;

namespace Accounting101.Payables.Api;

/// <summary>Create a vendor.</summary>
public sealed record CreateVendorRequest(string Name, string? Email);

/// <summary>Draft a bill. The line input reuses the domain <see cref="BillLineBody"/>. Number/Status/Id
/// are server-assigned and never sent.</summary>
public sealed record DraftBillRequest(
    Guid VendorId, DateOnly BillDate, DateOnly? DueDate, string? VendorReference, string? Memo,
    IReadOnlyList<BillLineBody> Lines);

/// <summary>Void an entered bill, with an optional reason.</summary>
public sealed record VoidReasonRequest(string? Reason);

/// <summary>Record a vendor payment with its allocations across bills.</summary>
public sealed record RecordBillPaymentRequest(
    Guid VendorId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);

/// <summary>Apply a vendor's existing credit to bills.</summary>
public sealed record VendorCreditApplicationRequest(
    Guid VendorId, DateOnly Date, IReadOnlyList<Allocation> Allocations);
