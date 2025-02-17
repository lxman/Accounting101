using System.Linq;
using DataAccess.Extensions;
using DataAccess.Interfaces;
using DataAccess.Services.Interfaces;

#pragma warning disable VSTHRD002

namespace DataAccess.Models;

public class ClientWithInfo : Client
{
    public PersonName? Name { get; set; }

    public IAddress? Address { get; set; }

    public ClientWithInfo()
    {

    }

    public ClientWithInfo(Client c, PersonName n, IAddress a)
    {
        BusinessName = c.BusinessName;
        Id = c.Id;
        PersonNameId = n.Id;
        AddressId = a.Id;
        Name = n;
        Address = a;
        CheckPointId = c.CheckPointId;
    }

    public ClientWithInfo(IDataStore dataStore, string dbName, Client c) : base(c)
    {
        Name = dataStore.ReadOneAsync<PersonName>(dbName, c.PersonNameId).Result?.FirstOrDefault();
        Address = dataStore.ReadOneAsync<IAddress>(dbName, c.AddressId).Result?.FirstOrDefault();
        CheckPointId = c.CheckPointId;
    }
}