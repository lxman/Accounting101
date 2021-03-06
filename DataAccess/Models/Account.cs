using System;

namespace DataAccess.Models
{
    public class Account
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid Client { get; set; }

        public Guid Info { get; set; }

        public decimal StartBalance { get; set; }

        public bool IsDebitAccount { get; set; }

        public DateTime Posted { get; set; } = DateTime.UtcNow;

        public Account()
        {
        }

        public Account(AccountWInfo acct)
        {
            Id = acct.Id;
            Info = acct.Info.Id;
            StartBalance = acct.StartBalance;
            IsDebitAccount = acct.IsDebitAccount;
            Posted = acct.Posted;
        }
    }
}