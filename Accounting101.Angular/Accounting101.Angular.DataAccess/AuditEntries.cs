using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Models.Auditing;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.DataAccess;

public static class AuditEntries
{
    public static async Task CreateAuditEntryAsync(this IDataStore store, string dbName, AuditEntry entry)
    {
        await store.CreateOneAsync(dbName, entry);
    }
}