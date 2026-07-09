namespace Accounting101.Ledger.Contracts;

/// <summary>
/// Wire contract for creating or updating a chart-of-accounts account (upsert by route id). The host
/// validates the resulting chart before persisting, so the chart is always structurally sound.
/// </summary>
public sealed record AccountRequest
{
    public required string Number { get; init; }
    public required string Name { get; init; }

    /// <summary>Asset | Liability | Equity | Revenue | Expense.</summary>
    public required string Type { get; init; }

    public Guid? ParentId { get; init; }
    public bool Postable { get; init; } = true;

    /// <summary>The dimension type a control account requires on every posting line (e.g. "Customer").
    /// A free-string axis the engine never interprets; omit for a non-control account. Legacy single-value
    /// form — still honored, but <see cref="RequiredDimensions"/> wins if present.</summary>
    public string? RequiredDimension { get; init; }

    /// <summary>The full set of dimension types a control account requires on every posting line (e.g.
    /// A/R → ["Customer", "Invoice"]). Optional; when present, wins over the legacy single
    /// <see cref="RequiredDimension"/>.</summary>
    public IReadOnlyList<string>? RequiredDimensions { get; init; }

    /// <summary>Cash | Operating | Investing | Financing — the statement-of-cash-flows bucket. Omit to use
    /// the type-based default; set Cash on the cash accounts and the investing/financing exceptions.</summary>
    public string? CashFlowActivity { get; init; }

    public bool IsRetainedEarnings { get; init; }
    public bool Active { get; init; } = true;
}

public sealed record AccountResponse(
    Guid Id,
    string Number,
    string Name,
    string Type,
    Guid? ParentId,
    bool Postable,
    string? RequiredDimension,
    IReadOnlyList<string> RequiredDimensions,
    string? CashFlowActivity,
    bool IsRetainedEarnings,
    bool Active,
    string NormalSide,
    bool IsTemporary);
