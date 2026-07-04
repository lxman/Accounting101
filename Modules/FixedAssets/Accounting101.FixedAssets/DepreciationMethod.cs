namespace Accounting101.FixedAssets;

/// <summary>How an asset depreciates. Multi-member from the start so FA-2 can add a pluggable strategy
/// without a data migration; FA-1 stores and validates the choice but computes nothing. 0-default so a
/// legacy document reads as straight-line.</summary>
public enum DepreciationMethod
{
    StraightLine = 0,
    DecliningBalance = 1,
}
