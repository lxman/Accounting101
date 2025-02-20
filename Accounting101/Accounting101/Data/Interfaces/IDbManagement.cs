namespace Accounting101.Data.Interfaces;

public interface IDbManagement
{
    Task CreateCustomerDatabaseAsync(Guid id);
    Task DropCustomerDatabaseAsync(Guid id);
    Task<bool> CustomerDatabaseExistsAsync(Guid id);
}