using System;
using DataAccess.Interfaces;
using MongoDB.Bson.Serialization.Attributes;

namespace DataAccess.Models.Auditing;

public class AuditEntry : IModel
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public string Message { get; set; } = string.Empty;
}