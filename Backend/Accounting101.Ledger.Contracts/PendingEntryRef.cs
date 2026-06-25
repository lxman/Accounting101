namespace Accounting101.Ledger.Contracts;

/// <summary>One entry blocking a period close: dated in the period, still awaiting approval.</summary>
public sealed record PendingEntryRef(Guid EntryId, string? Reference, DateOnly EffectiveDate, string Type);
