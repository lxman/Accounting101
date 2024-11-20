using System.Collections.Generic;
using System.Linq;
using DataAccess.Services.Interfaces;
using DataAccess.ZipCodeData;
using LiteDB;

namespace DataAccess.Models
{
    public class UsStates
    {
        public List<string> States { get; }

        public UsStates(IDataStore dataStore)
        {
            ILiteCollection<ZipCodeEntry>? zipEntries = dataStore.GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo);
            States = zipEntries?.FindAll().Select(e => e.State).Distinct().ToList() ?? [];
        }
    }
}