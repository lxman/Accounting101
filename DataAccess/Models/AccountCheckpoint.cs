using System;
using DataAccess.Interfaces;

namespace DataAccess.Models;

public class AccountCheckpoint(Guid clientId, Guid accountId, decimal balance) : IModel
{
    public Guid Id { get; set; }

    public Guid ClientId { get; init; } = clientId;

    public Guid AccountId { get; init; } = accountId;

    public decimal Balance { get; init; } = balance;
}