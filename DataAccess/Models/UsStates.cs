using System.Collections.Generic;
using System.Linq;
using DataAccess.Services.Interfaces;
using DataAccess.ZipCodeData;
using LiteDB.Async;
#pragma warning disable VSTHRD002

namespace DataAccess.Models
{
    public class UsStates
    {
        public List<string> States { get; }

        public UsStates(IDataStore dataStore)
        {
            ILiteCollectionAsync<ZipCodeEntry>? zipEntries = dataStore.GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo);
            States = zipEntries?.FindAllAsync().GetAwaiter().GetResult().Select(e => e.State).Distinct().ToList() ?? [];
        }
    }
}