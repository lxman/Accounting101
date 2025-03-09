using System;

namespace DataAccess.WPF.Models;

public class Client
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