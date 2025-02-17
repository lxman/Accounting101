using System.Threading.Tasks;
using DataAccess.Extensions;
using DataAccess.Models.Auditing;
using DataAccess.Services.Interfaces;

namespace DataAccess;

public static class AuditEntries
{
    public static async Task CreateAuditEntryAsync(this IDataStore store, string dbName, AuditEntry entry)
    {
        await store.CreateOneAsync(dbName, entry);
    }
}