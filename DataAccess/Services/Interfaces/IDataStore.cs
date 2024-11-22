using System;
using System.Collections.Generic;
using DataAccess.Models;
using LiteDB;

namespace DataAccess.Services.Interfaces
{
    public interface IDataStore
    {
        event EventHandler<ChangeEventArgs> StoreChanged;

        void NotifyChanged(Type t) { }

        LiteDatabase? Instance();

        ILiteCollection<T>? GetCollection<T>(string name);

        Business? GetBusiness();

        List<string> GetStates();

        BsonValue AddItem<T>(T item);

        void Dispose();
    }
}