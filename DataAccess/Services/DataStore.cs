using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.Models;
using DataAccess.Models.Auditing;
using DataAccess.Services.Interfaces;
using DataAccess.ZipCodeData;
using Microsoft.VisualStudio.Threading;
using Microsoft.Win32;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

#pragma warning disable VSTHRD002

#pragma warning disable CA1416

#pragma warning disable CS8618, CS9264

namespace DataAccess.Services;

public class DataStore : IDataStore, IDisposable
{
    public event EventHandler<ChangeEventArgs>? StoreChanged;

    public bool Initialized { get; private set; }

    private MongoClient? _db;
    private bool _disposedValue;
    private List<string>? _statesCached;

    public DataStore()
    {
        BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        ObjectSerializer objectSerializer = new(type => ObjectSerializer.DefaultAllowedTypes(type) || true);
        BsonSerializer.RegisterSerializer(objectSerializer);
        if (!IsDbRegistered())
        {
            return;
        }

        CreateOrOpenDatabase();
    }

    /// <summary>
    /// Constructor for unit testing purposes
    /// </summary>
    /// <param name="connString"></param>
    /// <exception cref="DataException"></exception>
    public DataStore(string connString)
    {
        _db ??= new MongoClient(connString);
        if (_db is null || !InitZipCodeDataAsync().GetAwaiter().GetResult()) throw new DataException("Error setting up database");
    }

    public void InitDatabase()
    {
        CreateOrOpenDatabase();
        Initialized = true;
    }

    public void CreateDatabase(string dbName)
    {
        CreateOrOpenDatabase();
        IMongoCollection<AuditEntry>? entries = _db?.GetDatabase(dbName).GetCollection<AuditEntry>(CollectionNames.AuditEntry);
        if (entries is null)
        {
            throw new DataException("Error creating the AuditEntry collection.");
        }
        entries.InsertOne(new AuditEntry { Message = "Database created" });
    }

    public void NotifyChange(Type t, ChangeType ct)
    {
        StoreChanged?.Invoke(this, new ChangeEventArgs { ChangedType = t, ChangeType = ct });
    }

    public IMongoDatabase? Instance(string dbName) => _db?.GetDatabase(dbName);

    public IMongoCollection<T>? GetCollection<T>(string dbName, string tableName) => _db?.GetDatabase(dbName).GetCollection<T>(tableName);

    public string GetDbLocation()
    {
        RegistryKey softwareKey = Registry.CurrentUser.OpenSubKey("Software")!;
        RegistryKey? jsKey = softwareKey.OpenSubKey("JordanSoft");
        RegistryKey? a101Key = jsKey?.OpenSubKey("Accounting101");
        return a101Key?.GetValue("DbLocation") as string ?? string.Empty;
    }

    public void ClearRegistry()
    {
        RegistryKey softwareKey = Registry.CurrentUser.OpenSubKey("Software")!;
        RegistryKey? jsKey = softwareKey.OpenSubKey("JordanSoft", true);
        jsKey?.DeleteSubKeyTree("Accounting101");
    }

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
        return await businesses.AsQueryable().CountAsync() == 1 ? await businesses.AsQueryable().FirstAsync() : null;
    }

    public async Task<List<string>> GetStatesAsync()
    {
        if (_statesCached?.Count > 0) return _statesCached;
        IMongoCollection<ZipCodeEntry>? collection = _db?.GetDatabase("ZipInfo").GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo);
        if (collection is null)
        {
            throw new DataException("Error accessing the ZipCodeEntry collection.");
        }

        _statesCached = (await collection.AsQueryable().Select(x => x.State).ToListAsync()).Distinct().ToList();

        return _statesCached;
    }

    private void CreateOrOpenDatabase()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString.ConnString))
        {
            return;
        }
        _db = new MongoClient();
        if (_db is null) throw new DataException("Error setting up database");
        JoinableTaskFactory jtf = new(new JoinableTaskCollection(new JoinableTaskContext()));
        if (jtf.Run(ZipCodeEntryCountAsync) == 0) jtf.Run(InitZipCodeDataAsync);
    }

    private bool IsDbRegistered()
    {
        return !string.IsNullOrWhiteSpace(GetDbLocation());
    }

    private async Task<int> ZipCodeEntryCountAsync()
    {
        return await _db?.GetDatabase("ZipInfo").GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo).AsQueryable().CountAsync()!;
    }

    private async Task<bool> InitZipCodeDataAsync()
    {
        List<string> data = (await File.ReadAllLinesAsync(@"ZipCodeData\ziplist5.txt")).ToList();
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
            _db?.Dispose();
        }

        _disposedValue = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}