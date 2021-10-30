using DataAccess.Models;
using DataAccess.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace DataAccess
{
    public static class PersonNames
    {
        public static Guid Create(this IDataStore store, PersonName name)
        {
            return store.GetCollection<PersonName>(CollectionNames.PersonNames)?.Insert(name).AsGuid ?? Guid.Empty;
        }

        public static void BulkInsert(this IDataStore store, IEnumerable<PersonName> names)
        {
            store.GetCollection<PersonName>(CollectionNames.PersonNames)?.InsertBulk(names);
        }

        public static PersonName? FindById(this IDataStore store, Guid id)
        {
            return store.GetCollection<PersonName>(CollectionNames.PersonNames)?.FindById(id);
        }
    }
}