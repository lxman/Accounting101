using DataAccess.Interfaces;
using DataAccess.Services.Interfaces;

namespace DataAccess.Models
{
    public class ClientWithInfo(IDataStore dataStore, Client c) : Client(c)
    {
        public PersonName? Name { get; set; } = dataStore.GetCollection<PersonName>(CollectionNames.PersonName)?.FindByIdAsync(c.PersonNameId).GetAwaiter().GetResult();

        public IAddress? Address { get; set; } = dataStore.GetCollection<IAddress>(CollectionNames.Address)?.FindByIdAsync(c.AddressId).GetAwaiter().GetResult();
    }
}