using System.Collections.Generic;
using DataAccess.WPF.Models;

namespace DataAccess.WPF;

public static class BalanceCalculator
{
    public static decimal Calculate(AccountWithInfo acct, IEnumerable<Transaction> transactions)
    {
        decimal balance = acct.StartBalance;
        foreach (Transaction transaction in transactions)
        {
            if (transaction.CreditedAccountId == acct.Id && !acct.IsDebitAccount
                || transaction.DebitedAccountId == acct.Id && acct.IsDebitAccount)
            {
                balance += transaction.Amount;
            }
            else if (transaction.CreditedAccountId == acct.Id && acct.IsDebitAccount
                     || transaction.DebitedAccountId == acct.Id && !acct.IsDebitAccount)
            {
                balance -= transaction.Amount;
            }
        }

        return balance;
    }
}