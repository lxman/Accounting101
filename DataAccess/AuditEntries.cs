using System.Threading.Tasks;
using DataAccess.Models;
using DataAccess.Models.Auditing;
using DataAccess.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess;

public static class AuditEntries
{
    public static async Task CreateAuditEntryAsync(this IDataStore store, AuditEntry entry)
    {
        ILiteCollectionAsync<AuditEntry>? entries = store.GetCollection<AuditEntry>(CollectionNames.AuditEntry);
        if (entries is null)
        {
            return;
        }
        await entries.InsertAsync(entry);
    }
}