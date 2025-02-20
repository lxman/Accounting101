using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DataAccess.Models;
using MongoDB.Driver;

namespace DataAccess.Services.Interfaces;

public interface IDataStore
{
    event EventHandler<ChangeEventArgs> StoreChanged;

    void NotifyChange(Type t, ChangeType ct);

    IMongoDatabase? Instance(string dbName);

    IMongoCollection<T>? GetCollection<T>(string dbName, string tableName);

    Task<bool> CreateBusinessAsync(string dbName, Business business);

    Task<Business?> GetBusinessAsync(string dbName);

    Task<bool> UpdateBusinessAsync(string dbName, Business business);

    Task<List<string>> GetStatesAsync();

    Task<List<string>> GetCountriesAsync();

    Task<bool> DatabaseExistsAsync(string dbName);

    Task DropDatabaseAsync(string dbName);

    void Dispose();
}