using System;
using Accounting101.Angular.DataAccess.Interfaces;

namespace Accounting101.Angular.DataAccess.Models;

public class AccountCheckpoint(string clientId, string accountId, decimal balance) : IGlobalItem
{
    public Guid Id { get; set; }

    public string ClientId { get; init; } = clientId;

    public string AccountId { get; init; } = accountId;

    public decimal Balance { get; init; } = balance;
}