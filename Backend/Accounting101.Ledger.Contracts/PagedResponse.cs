namespace Accounting101.Ledger.Contracts;

/// <summary>A page of a list plus the total matching the filter, so a UI can render page counts and jump to
/// a page. page count = ceil(Total/Limit); current page = Skip/Limit + 1; hasMore = Skip + Items.Count &lt; Total.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, long Total, int Skip, int Limit);
