using DataAccess.Interfaces;
using DataAccess.Services.Interfaces;

namespace DataAccess.Models
{
    public class ClientWithInfo : Client
    {
        public PersonName? Name { get; set; }

        public IAddress? Address { get; set; }

        public ClientWithInfo(IDataStore dataStore, Client c) : base(c)
        {
            Name = dataStore.GetCollection<PersonName>(CollectionNames.PersonName)?.FindById(c.PersonNameId);
            Address = dataStore.GetCollection<IAddress>(CollectionNames.Address)?.FindById(c.AddressId);
        }
    }
}