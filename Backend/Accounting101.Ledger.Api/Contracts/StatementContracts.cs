namespace Accounting101.Ledger.Api.Contracts;

/// <summary>One statement line: an account (or a synthesized total, when <see cref="AccountId"/> is null)
/// and its amount on the account's natural side.</summary>
public sealed record StatementLineResponse(Guid? AccountId, string? Number, string Name, decimal Amount);

/// <summary>A titled group of lines and its total.</summary>
public sealed record StatementSectionResponse(string Title, IReadOnlyList<StatementLineResponse> Lines, decimal Total);

/// <summary>The balance sheet as of a date. <see cref="IsBalanced"/> is the Assets = Liabilities + Equity check.</summary>
public sealed record BalanceSheetResponse(
    DateOnly AsOf,
    StatementSectionResponse Assets,
    StatementSectionResponse Liabilities,
    StatementSectionResponse Equity,
    decimal TotalAssets,
    decimal TotalLiabilitiesAndEquity,
    bool IsBalanced);

/// <summary>The income statement for a period.</summary>
public sealed record IncomeStatementResponse(
    DateOnly From,
    DateOnly To,
    StatementSectionResponse Revenue,
    StatementSectionResponse Expenses,
    decimal NetIncome);

/// <summary>The statement of cash flows for a period (indirect method). <see cref="TiesOut"/> asserts the
/// three sections explain the actual <see cref="EndingCash"/> − <see cref="BeginningCash"/> movement.</summary>
public sealed record CashFlowStatementResponse(
    DateOnly From,
    DateOnly To,
    decimal NetIncome,
    StatementSectionResponse OperatingAdjustments,
    decimal OperatingCash,
    StatementSectionResponse Investing,
    StatementSectionResponse Financing,
    decimal NetChangeInCash,
    decimal BeginningCash,
    decimal EndingCash,
    bool TiesOut);
