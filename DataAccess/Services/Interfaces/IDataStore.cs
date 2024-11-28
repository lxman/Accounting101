using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DataAccess.Models;
using LiteDB;
using LiteDB.Async;

namespace DataAccess.Services.Interfaces
{
    public interface IDataStore
    {
        event EventHandler<ChangeEventArgs> StoreChanged;

        void NotifyChanged(Type t) { }

        LiteDatabaseAsync? Instance();

        ILiteCollectionAsync<T>? GetCollection<T>(string name);

        Task<bool> CreateBusinessAsync(Business business);

        Task<Business?> GetBusinessAsync();

        Task<List<string>> GetStatesAsync();

        Task<BsonValue> AddItemAsync<T>(T item);

        void Dispose();
    }
}