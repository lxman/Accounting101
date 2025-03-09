using System;
using Accounting101.Angular.DataAccess.Interfaces;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Angular.DataAccess.Models;

[BsonKnownTypes(typeof(ClientWithInfo))]
public class Client : IModel
{
    public Guid Id { get; set; }

    public string BusinessName { get; set; } = string.Empty;

    public Guid PersonNameId { get; set; }

    public Guid AddressId { get; set; }

    public Guid? CheckPointId { get; set; }

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