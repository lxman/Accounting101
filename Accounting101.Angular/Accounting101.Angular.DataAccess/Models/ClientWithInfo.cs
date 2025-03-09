using System.Linq;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Interfaces;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable VSTHRD002

namespace Accounting101.Angular.DataAccess.Models;

public class ClientWithInfo : Client
{
    public PersonName? Name { get; set; }

    public IAddress? Address { get; set; }

    public ClientWithInfo()
    {

    }

    private readonly JoinableTaskFactory _jtf = new(new JoinableTaskCollection(new JoinableTaskContext()));

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
        Name = _jtf.Run(() => dataStore.ReadOneAsync<PersonName>(dbName, c.PersonNameId))?.FirstOrDefault();
        Address = dataStore.FindAddressById(dbName, AddressId);
        CheckPointId = c.CheckPointId;
    }
}