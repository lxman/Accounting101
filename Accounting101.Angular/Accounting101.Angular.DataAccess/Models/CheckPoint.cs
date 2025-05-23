﻿using System;
using System.Collections.Generic;
using Accounting101.Angular.DataAccess.Interfaces;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Angular.DataAccess.Models;

public class CheckPoint(string clientId, DateOnly date) : IClientItem
{
    [BsonId]
    public Guid Id { get; set; }

    public string ClientId { get; init; } = clientId;

    public DateOnly Date { get; init; } = date;

    [BsonIgnore]
    public List<AccountCheckpoint> AccountCheckpoints { get; } = [];
}