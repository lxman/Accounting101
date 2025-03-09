using System;

namespace DataAccess.WPF.Models;

public class AccountCheckpoint(Guid clientId, Guid accountId, decimal balance)
{
    public Guid ClientId { get; init; } = clientId;

    public Guid AccountId { get; init; } = accountId;

    public decimal Balance { get; init; } = balance;
}