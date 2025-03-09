using System.Collections.Generic;

namespace DataAccess.WPF.Models;

public class AccountWithEverything
{
    public Account Account { get; } = new();

    public AccountInfo Info { get; } = new();

    public List<Transaction> Transactions { get; } = [];

    public CheckPoint? CheckPoint { get; init; }

    public AccountWithEverything(AccountWithInfo acctWithInfo, List<Transaction> transactions, CheckPoint? checkPoint)
    {
        Account.Id = acctWithInfo.Id;
        Account.ClientId = acctWithInfo.ClientId;
        Account.InfoId = acctWithInfo.InfoId;
        Account.StartBalance = acctWithInfo.StartBalance;
        Account.Type = acctWithInfo.Type;
        Account.Created = acctWithInfo.Created;
        Info.Id = acctWithInfo.Info.Id;
        Info.Name = acctWithInfo.Info.Name;
        Info.CoAId = acctWithInfo.Info.CoAId;
        Transactions.AddRange(transactions);
        CheckPoint = checkPoint;
    }
}