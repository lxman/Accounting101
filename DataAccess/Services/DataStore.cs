using DataAccess.Services.Interfaces;
using LiteDB;
using System;

namespace DataAccess.Services
{
    public class DataStore : IDataStore, IDisposable
    {
        public event EventHandler<ChangeEventArgs> StoreChanged;

        private readonly LiteDatabase? Db;
        private bool DisposedValue;

        public DataStore()
        {
            Db ??= new LiteDatabase(ConnectionString.ConnString);
        }

        public void NotifyChange(Type t)
        {
            StoreChanged?.Invoke(null, new ChangeEventArgs {ChangedType = t});
        }

        public LiteDatabase? Instance()
        {
            return Db;
        }

        public ILiteCollection<T>? GetCollection<T>(string name)
        {
            return Db?.GetCollection<T>(name);
        }

        public BsonValue AddItem<T>(T item)
        {
            if (Db?.CollectionExists(typeof(T).Name) ?? false)
            {
                return Db?.GetCollection<T>()?.Insert(item) ?? false;
            }

            return false;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (DisposedValue)
            {
                return;
            }

            if (disposing)
            {
                Db?.Dispose();
            }

            DisposedValue = true;
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}