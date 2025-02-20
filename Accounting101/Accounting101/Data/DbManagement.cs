using Accounting101.Data.Interfaces;
using DataAccess;
using DataAccess.Models.Auditing;
using DataAccess.Services.Interfaces;

namespace Accounting101.Data;

public class DbManagement(IDataStore dataStore) : IDbManagement
{
    public async Task CreateCustomerDatabaseAsync(Guid id)
    {
        string dbName = id.ToString();
        AuditEntry entry = new() { Message = "Initial database creation" };
        await dataStore.CreateAuditEntryAsync(dbName, entry);
    }

    public async Task DropCustomerDatabaseAsync(Guid id)
    {
        string dbName = id.ToString();
        await dataStore.DropDatabaseAsync(dbName);
    }

    public async Task<bool> CustomerDatabaseExistsAsync(Guid id)
    {
        string dbName = id.ToString();
        return await dataStore.DatabaseExistsAsync(dbName);
    }
}
