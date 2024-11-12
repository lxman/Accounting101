using System;
using DataAccess.Services.Interfaces;
using LiteDB;

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
        }

        public DataStore(string connString)
        {
            _db ??= new LiteDatabase(connString);
        }

        public void NotifyChange(Type t)
        {
            StoreChanged(null, new ChangeEventArgs { ChangedType = t });
        }

        public LiteDatabase? Instance() => _db;

        public ILiteCollection<T>? GetCollection<T>(string name) => _db?.GetCollection<T>(name);

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
    }
}