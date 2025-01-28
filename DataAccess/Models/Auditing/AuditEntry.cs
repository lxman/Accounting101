using System;
using LiteDB;

namespace DataAccess.Models.Auditing;

public class AuditEntry
{
    [BsonId]
    Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public string Message { get; set; } = string.Empty;
}