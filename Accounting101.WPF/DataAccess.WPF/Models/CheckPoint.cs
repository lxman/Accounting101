using System;
using System.Collections.Generic;
using LiteDB;

namespace DataAccess.WPF.Models;

public class CheckPoint(Guid clientId, DateOnly date)
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid ClientId { get; init; } = clientId;

    public DateOnly Date { get; init; } = date;

    [BsonIgnore]
    public List<AccountCheckpoint> AccountCheckpoints { get; } = [];
}