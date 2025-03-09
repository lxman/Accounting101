using DataAccess.WPF.Interfaces;
using DataAccess.WPF.Services.Interfaces;

#pragma warning disable VSTHRD002

namespace DataAccess.WPF.Models;

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

    public ClientWithInfo(IDataStore dataStore, Client c) : base(c)
    {
        Name = dataStore.GetCollection<PersonName>(CollectionNames.PersonName)?.FindByIdAsync(c.PersonNameId).GetAwaiter().GetResult();
        Address = dataStore.GetCollection<IAddress>(CollectionNames.Address)?.FindByIdAsync(c.AddressId).GetAwaiter().GetResult();
        CheckPointId = c.CheckPointId;
    }
}