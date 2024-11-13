using DataAccess.Interfaces;
using DataAccess.Services.Interfaces;

namespace DataAccess.Models
{
    public class ClientWithInfo(IDataStore dataStore, Client c) : Client
    {
        public PersonName? Name { get; set; } = dataStore.GetCollection<PersonName>(CollectionNames.PersonNames)?.FindById(c.NameId);

        public IAddress? Address { get; set; } = dataStore.GetCollection<IAddress>(CollectionNames.Addresses)?.FindById(c.AddressId);
    }
}