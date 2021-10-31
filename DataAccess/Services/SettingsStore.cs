using DataAccess.Services.Interfaces;
using LiteDB;
using System;
using System.IO;

namespace DataAccess.Services
{
    public class SettingsStore : ISettingsStore
    {
        private readonly LiteDatabase? Db;
        private bool DisposedValue;
        private readonly string SettingsPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        public SettingsStore()
        {
            Db ??= new LiteDatabase($"Filename={Path.Combine(SettingsPath, "Settings")};Password=1234;");
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