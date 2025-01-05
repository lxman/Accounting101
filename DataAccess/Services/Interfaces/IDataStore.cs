using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DataAccess.Models;
using LiteDB.Async;

namespace DataAccess.Services.Interfaces
{
    public interface IDataStore
    {
        event EventHandler<ChangeEventArgs> StoreChanged;

        void NotifyChange(Type t, ChangeType ct);

        LiteDatabaseAsync? Instance();

        ILiteCollectionAsync<T>? GetCollection<T>(string name);

        void InitDatabase();

        void CreateDatabase(string location);

        Task<bool> CreateBusinessAsync(Business business);

        Task<Business?> GetBusinessAsync();

        Task<bool> UpdateBusinessAsync(Business business);

        Task<List<string>> GetStatesAsync();

        string GetDbLocation();

        void ClearRegistry();

        void Dispose();
    }
}