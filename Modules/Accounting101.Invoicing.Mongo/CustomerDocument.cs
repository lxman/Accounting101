using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Invoicing.Mongo;

/// <summary>Mongo storage shape for a <see cref="Customer"/>.</summary>
public sealed class CustomerDocument
{
    [BsonId]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }

    public static CustomerDocument FromDomain(Customer c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Email = c.Email,
    };

    public Customer ToDomain() => new()
    {
        Id = Id,
        Name = Name,
        Email = Email,
    };
}
