using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using DataAccess.ZipCodeData;
using LiteDB;
#pragma warning disable CS8618, CS9264

namespace DataAccess.Services
{
    public class DataStore : IDataStore, IDisposable
    {
        public event EventHandler<ChangeEventArgs> StoreChanged;

        private readonly LiteDatabase? _db;
        private bool _disposedValue;

        public DataStore()
        {
            _db ??= new LiteDatabase(ConnectionString.ConnString);
            if (_db is null) throw new DataException("Error setting up database");
            if (ZipCodeEntryCount() == 0) InitZipCodeData();
        }

        /// <summary>
        /// Constructor for unit testing purposes
        /// </summary>
        /// <param name="connString"></param>
        /// <exception cref="DataException"></exception>
        public DataStore(string connString)
        {
            _db ??= new LiteDatabase(connString);
            if (_db is null || !InitZipCodeData()) throw new DataException("Error setting up database");
        }

        public void NotifyChange(Type t)
        {
            StoreChanged(null, new ChangeEventArgs { ChangedType = t });
        }

        public LiteDatabase? Instance() => _db;

        public ILiteCollection<T>? GetCollection<T>(string name) => _db?.GetCollection<T>(name);

        public Business? GetBusiness()
        {
            List<Business> businesses = _db?.GetCollection<Business>()?.FindAll().ToList() ?? [];
            return businesses.Count == 1 ? businesses[0] : null;
        }

        public List<string> GetStates()
        {
            ILiteCollection<ZipCodeEntry>? collection = _db?.GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo);
            if (collection is null)
            {
                throw new DataException("Error accessing the ZipCodeEntry collection.");
            }

            List<string> states = collection.Query().Select(x => x.State).ToList().Distinct().ToList();

            return states;
        }

        public BsonValue AddItem<T>(T item)
        {
            if (_db?.CollectionExists(typeof(T).Name) ?? false)
            {
                return _db?.GetCollection<T>()?.Insert(item) ?? false;
            }

            return false;
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

        private int ZipCodeEntryCount()
        {
            return _db?.GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo).Count() ?? 0;
        }

        private bool InitZipCodeData()
        {
            List<string> data = File.ReadAllLines(@"ZipCodeData\ziplist5.txt").ToList();
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
            ILiteCollection<ZipCodeEntry>? zipCollection = _db?.GetCollection<ZipCodeEntry>(CollectionNames.ZipInfo);
            if (zipCollection is null)
            {
                return false;
            }
            zipCollection.InsertBulk(entries);
            return true;
        }
    }
}