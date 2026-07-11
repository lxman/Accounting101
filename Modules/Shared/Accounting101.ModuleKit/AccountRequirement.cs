namespace Accounting101.ModuleKit;

/// <summary>The status of one required account against a client's chart.</summary>
public enum AccountReadinessStatus { Ok, Missing, Inactive, WrongType, MissingDimensions }

/// <summary>A module's declared expectation for one chart account it posts to or folds.
/// <paramref name="ExpectedType"/> null = don't check type; <paramref name="RequiredDimensions"/>
/// empty = the account only needs to exist.</summary>
public sealed record AccountRequirement(
    Guid AccountId,
    string Label,
    string? ExpectedType,
    IReadOnlyList<string> RequiredDimensions);

/// <summary>The evaluation of one <see cref="AccountRequirement"/> against the chart.</summary>
public sealed record AccountReadinessResult(
    Guid AccountId,
    string Label,
    string? ExpectedType,
    IReadOnlyList<string> RequiredDimensions,
    AccountReadinessStatus Status,
    string? ActualType,
    IReadOnlyList<string>? ActualRequiredDimensions,
    string Detail);

/// <summary>The readiness of a client's chart for one module. <see cref="Ready"/> is true iff every
/// account is <see cref="AccountReadinessStatus.Ok"/>.</summary>
public sealed record ChartReadinessReport(
    string ModuleKey,
    bool Ready,
    IReadOnlyList<AccountReadinessResult> Accounts);
