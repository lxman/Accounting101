using System.Threading.Tasks;
using DataAccess.WPF.Models;
using DataAccess.WPF.Models.Auditing;
using DataAccess.WPF.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess.WPF;

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