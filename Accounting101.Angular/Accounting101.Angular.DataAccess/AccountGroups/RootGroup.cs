using System;
using Accounting101.Angular.DataAccess.Interfaces;
using Accounting101.Angular.DataAccess.Models;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Angular.DataAccess.AccountGroups;

public class RootGroup : IClientItem
{
    [BsonId]
    public Guid Id { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public AccountGroup Assets { get; set; } = new();

    public AccountGroup Liabilities { get; set; } = new();

    public AccountGroup Equity { get; set; } = new();

    public AccountGroup Revenue { get; set; } = new();

    public AccountGroup Expenses { get; set; } = new();

    public AccountGroup Earnings { get; set; } = new();
}