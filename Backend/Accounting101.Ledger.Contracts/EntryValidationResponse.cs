namespace Accounting101.Ledger.Contracts;

/// <summary>
/// Response from the side-effect-free validation dry-run
/// (<c>POST /clients/{clientId}/entries/validate</c>). A <c>true</c> result means the entry would
/// post successfully; a rejection returns the same ProblemDetails the real post returns, not this DTO.
/// </summary>
public sealed record EntryValidationResponse(bool Valid);
