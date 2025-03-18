using System;
using Accounting101.Angular.DataAccess.Interfaces;

namespace Accounting101.Angular.DataAccess.Models;

public class Account : IClientItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public BaseAccountTypes Type { get; set; }

    public Guid ClientId { get; set; }

    public Guid InfoId { get; set; }

    public decimal StartBalance { get; set; }

    public bool IsDebitAccount => Type is BaseAccountTypes.Asset or BaseAccountTypes.Expense;

    public DateOnly Created { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    public Account()
    {
    }

    public Account(AccountWithInfo acct)
    {
        Id = acct.Id;
        Type = acct.Type;
        ClientId = acct.ClientId;
        InfoId = acct.InfoId;
        StartBalance = acct.StartBalance;
        Created = acct.Created;
    }
}