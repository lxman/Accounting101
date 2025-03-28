using System.Collections.Generic;
using Accounting101.Angular.DataAccess.Models;

namespace Accounting101.Angular.DataAccess;

public static class BalanceCalculator
{
    public static decimal Calculate(AccountWithInfo acct, IEnumerable<Transaction> transactions)
    {
        decimal balance = acct.StartBalance;
        foreach (Transaction transaction in transactions)
        {
            if (transaction.CreditedAccountId == acct.Id.ToString() && !acct.IsDebitAccount
                || transaction.DebitedAccountId == acct.Id.ToString() && acct.IsDebitAccount)
            {
                balance += transaction.Amount;
            }
            else if (transaction.CreditedAccountId == acct.Id.ToString() && acct.IsDebitAccount
                     || transaction.DebitedAccountId == acct.Id.ToString() && !acct.IsDebitAccount)
            {
                balance -= transaction.Amount;
            }
        }

        return balance;
    }
}