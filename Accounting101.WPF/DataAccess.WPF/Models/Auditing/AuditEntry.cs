using System;
using LiteDB;

namespace DataAccess.WPF.Models.Auditing;

public class AuditEntry
{
    [BsonId]
    private Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public string Message { get; set; } = string.Empty;
}