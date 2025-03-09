using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.CountryData;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Accounting101.Angular.DataAccess.ZipCodeData;
using Microsoft.VisualStudio.Threading;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

// ReSharper disable StringLiteralTypo

#pragma warning disable VSTHRD002

#pragma warning disable CA1416

#pragma warning disable CS8618, CS9264

namespace Accounting101.Angular.DataAccess.Services;

public class DataStore : IDataStore, IDisposable
{
    public event EventHandler<ChangeEventArgs>? StoreChanged;

    private readonly MongoClient? _db;
    private bool _disposedValue;
    private List<string>? _statesCached;
    private List<string>? _countriesCached;

    public DataStore(string connString, bool unitTesting = false)
    {
        if (unitTesting)
        {
            _db ??= new MongoClient(connString);
            return;
        }
        ObjectSerializer objectSerializer = new(type => ObjectSerializer.DefaultAllowedTypes(type) || true);
        BsonSerializer.RegisterSerializer(objectSerializer);
        BsonClassMap.RegisterClassMap<UsAddress>();
        BsonClassMap.RegisterClassMap<ForeignAddress>();
        BsonClassMap.RegisterClassMap<ClientWithInfo>();
        _db = new MongoClient(connString);
        JoinableTaskFactory jtf = new(new JoinableTaskCollection(new JoinableTaskContext()));
        if (jtf.Run(ZipCodeEntryCountAsync) == 0) jtf.Run(InitZipCodeDataAsync);
        if (jtf.Run(CountryInfoCountAsync) == 0) jtf.Run(InitCountryDataAsync);
    }

    public void NotifyChange(Type t, ChangeType ct)
    {
        StoreChanged?.Invoke(this, new ChangeEventArgs { ChangedType = t, ChangeType = ct });
    }

    public Task DropDatabaseAsync(string dbName) => _db?.DropDatabaseAsync(dbName) ?? Task.CompletedTask;

    public async Task<bool> DatabaseExistsAsync(string dbName)
    {
        if (_db is null) return false;
        IAsyncCursor<string>? cursor = await _db.ListDatabaseNamesAsync();
        return (await cursor.ToListAsync()).Contains(dbName);
    }

    public IMongoDatabase? Instance(string dbName) => _db?.GetDatabase(dbName);

    public IMongoCollection<T>? GetCollection<T>(string dbName, string tableName) => _db?.GetDatabase(dbName).GetCollection<T>(tableName);

    public async Task<bool> CreateBusinessAsync(string dbName, Business business)
    {
        if (_db is null) return false;
        await _db.GetDatabase(dbName).GetCollection<Business>(CollectionNames.Business).InsertOneAsync(business);
        NotifyChange(typeof(Business), ChangeType.Created);
        return true;
    }

    public async Task<bool> UpdateBusinessAsync(string dbName, Business business)
    {
        if (_db is null) return false;
        FilterDefinition<Business>? filter = Builders<Business>.Filter.Eq(x => x.Id, business.Id);
        UpdateDefinition<Business>? update = Builders<Business>.Update
            .Set(x => x.Name, business.Name)
            .Set(x => x.Address, business.Address);
        UpdateResult? result = await _db.GetDatabase(dbName).GetCollection<Business>(CollectionNames.Business).UpdateOneAsync(filter, update);
        if (result.IsAcknowledged) NotifyChange(typeof(Business), ChangeType.Updated);
        return result.IsAcknowledged;
    }

    public async Task<Business?> GetBusinessAsync(string dbName)
    {
        IMongoCollection<Business>? businesses = _db?.GetDatabase(dbName).GetCollection<Business>(CollectionNames.Business);
        if (businesses is null)
        {
            return null;
        }

        return await businesses.CountDocumentsAsync(x => x.Id != ObjectId.Empty) == 1
            ? await businesses.AsQueryable().FirstOrDefaultAsync()
            : null;
    }

    public async Task<List<string>> GetStatesAsync()
    {
        if (_statesCached?.Count > 0) return _statesCached;
        IMongoCollection<ZipCodeEntry>? collection = _db?.GetDatabase("ZipInfo").GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo);
        if (collection is null)
        {
            throw new DataException("Error accessing the ZipCodeEntry collection.");
        }

        _statesCached = await collection.AsQueryable().Select(x => x.State).Distinct().ToListAsync();

        return _statesCached;
    }

    private async Task<int> CountryInfoCountAsync() =>
        await _db?.GetDatabase("CountryInfo").GetCollection<CountryList>(CollectionNames.CountryInfo).AsQueryable().CountAsync()!;

    public async Task<List<string>> GetCountriesAsync()
    {
        if (_countriesCached?.Count > 0) return _countriesCached;
        IMongoCollection<CountryList>? collection = _db?.GetDatabase("CountryInfo").GetCollection<CountryList>(CollectionNames.CountryInfo);
        if (collection is null)
        {
            throw new DataException("Error accessing the ZipCodeEntry collection.");
        }

        _countriesCached = (await collection.AsQueryable().FirstAsync()).Countries;
        return _countriesCached;
    }

    private async Task<int> ZipCodeEntryCountAsync() =>
        await _db?.GetDatabase("ZipInfo").GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo).AsQueryable().CountAsync()!;

    private async Task<bool> InitCountryDataAsync()
    {
        IMongoCollection<CountryList>? countryCollection = _db?.GetDatabase("CountryInfo").GetCollection<CountryList>(CollectionNames.CountryInfo);
        if (countryCollection is null)
        {
            return false;
        }

        CountryList countries = new();
        countries.Countries.AddRange(Countries.Data);
        await countryCollection.InsertOneAsync(countries);
        return true;
    }

    private async Task<bool> InitZipCodeDataAsync()
    {
        List<string> data = (await File.ReadAllLinesAsync(@"..\DataAccess\ZipCodeData\ziplist5.txt")).ToList();
        List<ZipCodeEntry> entries = [];
        data.ForEach(d =>
        {
            string[] parts = d.Split(',');
            ZipCodeEntry e = new()
            {
                City = parts[0],
                State = parts[1],
                Zip = parts[2],
                AreaCode = parts[3],
                Fips = parts[4],
                County = parts[5]
            };
            entries.Add(e);
        });
        IMongoCollection<ZipCodeEntry>? zipCollection = _db?.GetDatabase("ZipInfo").GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo);
        if (zipCollection is null)
        {
            return false;
        }
        await zipCollection.InsertManyAsync(entries);
        return true;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposedValue)
        {
            return;
        }

        if (disposing)
        {
            //_db?.Dispose();
        }

        _disposedValue = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}