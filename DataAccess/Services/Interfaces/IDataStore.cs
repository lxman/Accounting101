using LiteDB;
using System;

namespace DataAccess.Services.Interfaces
{
    public interface IDataStore
    {
        static event EventHandler<ChangeEventArgs> StoreChanged;

        public void NotifyChanged(Type t);

        LiteDatabase? Instance();

        ILiteCollection<T>? GetCollection<T>(string name);

        BsonValue AddItem<T>(T item);

        void Dispose();
    }
}