// ReSharper disable CheckNamespace
#pragma warning disable CA1050

public enum BaseAccountTypes
{
    Asset,
    Expense,
    Liability,
    Equity,
    Revenue,
    Earnings
}

/*
   Type       Debit     Credit
   Asset      Increase  Decrease
   Expense    Increase  Decrease
   Loss	      Increase  Decrease
   Liability  Decrease  Increase
   Equity     Decrease  Increase
   Revenue    Decrease  Increase
   Gain       Decrease  Increase
 */