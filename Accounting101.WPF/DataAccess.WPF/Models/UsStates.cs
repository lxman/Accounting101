using System.Collections.Generic;
using System.Linq;
using DataAccess.WPF.Services.Interfaces;
using DataAccess.WPF.ZipCodeData;
using LiteDB.Async;

#pragma warning disable VSTHRD002

namespace DataAccess.WPF.Models;

public class UsStates
{
    public List<string> States { get; }

    public UsStates(IDataStore dataStore)
    {
        ILiteCollectionAsync<ZipCodeEntry>? zipEntries = dataStore.GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo);
        States = zipEntries?.FindAllAsync().GetAwaiter().GetResult().Select(e => e.State).Distinct().ToList() ?? [];
    }
}