namespace Accounting101.Ledger.Contracts;

/// <summary>One posting-account slot for a client, with its current account (null when unset).</summary>
public sealed record PostingAccountSlotResponse(
    string ModuleKey, string SlotKey, string Label, string ExpectedType,
    IReadOnlyList<string> RequiredDimensions, Guid? CurrentAccountId);

/// <summary>All posting-account slots for a client's enabled modules.</summary>
public sealed record PostingAccountsResponse(IReadOnlyList<PostingAccountSlotResponse> Slots);

/// <summary>Set a module's posting accounts: slot key → chart account id.</summary>
public sealed record SetPostingAccountsRequest(IReadOnlyDictionary<string, Guid> Slots);

/// <summary>A module's saved posting accounts, echoed back by the setter.</summary>
public sealed record PostingAccountsModuleResponse(string ModuleKey, IReadOnlyDictionary<string, Guid> Slots);
