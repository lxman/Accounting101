using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.WPF.Models;
using DataAccess.WPF.Models.Auditing;
using DataAccess.WPF.Services.Interfaces;
using DataAccess.WPF.ZipCodeData;
using LiteDB.Async;
using Microsoft.VisualStudio.Threading;
using Microsoft.Win32;

#pragma warning disable VSTHRD002

#pragma warning disable CA1416

#pragma warning disable CS8618, CS9264

namespace DataAccess.WPF.Services;

public class DataStore : IDataStore, IDisposable
{
    public event EventHandler<ChangeEventArgs>? StoreChanged;

    public bool Initialized { get; private set; }

    private LiteDatabaseAsync? _db;
    private bool _disposedValue;
    private List<string>? _statesCached;

    public DataStore()
    {
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
        _db ??= new LiteDatabaseAsync(connString);
        if (_db is null || !InitZipCodeDataAsync().GetAwaiter().GetResult()) throw new DataException("Error setting up database");
    }

    public void InitDatabase()
    {
        CreateOrOpenDatabase();
        Initialized = true;
    }

    public void CreateDatabase(string location)
    {
        string? filePath = Path.GetDirectoryName(location);
        if (filePath is null)
        {
            throw new DataException("Invalid file path for database.");
        }

        if (!Directory.Exists(filePath))
        {
            Directory.CreateDirectory(filePath);
        }
        CreateOrOpenDatabase();
        ILiteCollectionAsync<AuditEntry>? entries = _db?.GetCollection<AuditEntry>(CollectionNames.AuditEntry);
        if (entries is null)
        {
            throw new DataException("Error creating the AuditEntry collection.");
        }
        entries.InsertAsync(new AuditEntry() { Message = "Database created" }).GetAwaiter().GetResult();
    }

    public void NotifyChange(Type t, ChangeType ct)
    {
        StoreChanged?.Invoke(this, new ChangeEventArgs { ChangedType = t, ChangeType = ct });
    }

    public LiteDatabaseAsync? Instance() => _db;

    public ILiteCollectionAsync<T>? GetCollection<T>(string name) => _db?.GetCollection<T>(name);

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

    public async Task<bool> CreateBusinessAsync(Business business)
    {
        if (_db is null) return false;
        await _db.GetCollection<Business>().InsertAsync(business);
        NotifyChange(typeof(Business), ChangeType.Created);
        return true;
    }

    public async Task<bool> UpdateBusinessAsync(Business business)
    {
        if (_db is null) return false;
        bool result = await _db.GetCollection<Business>().UpdateAsync(business);
        if (result) NotifyChange(typeof(Business), ChangeType.Updated);
        return result;
    }

    public async Task<Business?> GetBusinessAsync()
    {
        List<Business> businesses = (await _db?.GetCollection<Business>()?.FindAllAsync()!).ToList() ?? [];
        return businesses.Count == 1 ? businesses[0] : null;
    }

    public async Task<List<string>> GetStatesAsync()
    {
        if (_statesCached?.Count > 0) return _statesCached;
        ILiteCollectionAsync<ZipCodeEntry>? collection = _db?.GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo);
        if (collection is null)
        {
            throw new DataException("Error accessing the ZipCodeEntry collection.");
        }

        _statesCached = (await collection.Query().Select(x => x.State).ToListAsync()).Distinct().ToList();

        return _statesCached;
    }

    private void CreateOrOpenDatabase()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString.ConnString))
        {
            return;
        }
        _db = new LiteDatabaseAsync(ConnectionString.ConnString);
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
        return await _db?.GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo).CountAsync()!;
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
        ILiteCollectionAsync<ZipCodeEntry>? zipCollection = _db?.GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo);
        if (zipCollection is null)
        {
            return false;
        }
        await zipCollection.InsertBulkAsync(entries);
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