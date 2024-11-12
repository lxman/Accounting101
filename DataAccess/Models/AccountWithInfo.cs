using System;

namespace DataAccess.Models
{
    public class AccountWithInfo
    {
        public Guid Id { get; set; }

        public AccountInfo Info { get; set; }

        public decimal StartBalance { get; set; }

        public bool IsDebitAccount { get; set; }

        public DateTime Posted { get; set; }

        public AccountWithInfo(Account acct, AccountInfo info)
        {
            Id = acct.Id;
            Info = info;
            StartBalance = acct.StartBalance;
            IsDebitAccount = acct.IsDebitAccount;
            Posted = acct.Created;
        }
    }
}