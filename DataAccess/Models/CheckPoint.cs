using System;
using System.Collections.Generic;
using DataAccess.Interfaces;
using MongoDB.Bson.Serialization.Attributes;

namespace DataAccess.Models;

public class CheckPoint(Guid clientId, DateOnly date) : IModel
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid ClientId { get; init; } = clientId;

    public DateOnly Date { get; init; } = date;

    [BsonIgnore]
    public List<AccountCheckpoint> AccountCheckpoints { get; } = [];
}