﻿using System;
using LiteDB;

namespace DataAccess.Services.Interfaces
{
    public interface IDataStore
    {
        event EventHandler<ChangeEventArgs> StoreChanged;

        void NotifyChanged(Type t) { }

        LiteDatabase? Instance();

        ILiteCollection<T>? GetCollection<T>(string name);

        BsonValue AddItem<T>(T item);

        void Dispose();
    }
}