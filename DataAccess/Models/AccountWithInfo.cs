﻿namespace DataAccess.Models
{
    public class AccountWithInfo : Account
    {
        public AccountInfo Info { get; set; }

        public AccountWithInfo(Account acct, AccountInfo info)
        {
            Id = acct.Id;
            ClientId = acct.ClientId;
            InfoId = acct.InfoId;
            Type = acct.Type;
            StartBalance = acct.StartBalance;
            Created = acct.Created;
            Info = info;
        }
    }
}