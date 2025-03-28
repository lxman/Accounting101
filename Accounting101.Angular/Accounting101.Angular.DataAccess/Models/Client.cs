using System;
using Accounting101.Angular.DataAccess.Interfaces;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Angular.DataAccess.Models;

[BsonKnownTypes(typeof(ClientWithInfo))]
public class Client : IGlobalItem
{
    public Guid Id { get; set; }

    public string BusinessName { get; set; } = string.Empty;

    public string PersonNameId { get; set; } = string.Empty;

    public string AddressId { get; set; } = string.Empty;

    public string? CheckPointId { get; set; }

    public Client()
    {
    }

    public Client(Client c)
    {
        Id = c.Id;
        BusinessName = c.BusinessName;
        PersonNameId = c.PersonNameId;
        AddressId = c.AddressId;
        CheckPointId = c.CheckPointId;
    }
}