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
            Guid result = store.GetCollection<PersonName>(CollectionNames.PersonNames)?.Insert(name).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(PersonNames));
            return result;
        }

        public static void BulkInsert(this IDataStore store, IEnumerable<PersonName> names)
        {
            int? result = store.GetCollection<PersonName>(CollectionNames.PersonNames)?.InsertBulk(names);
            if (result > 0) store.NotifyChanged(typeof(PersonNames));
        }

        public static PersonName? FindById(this IDataStore store, Guid id)
        {
            return store.GetCollection<PersonName>(CollectionNames.PersonNames)?.FindById(id);
        }
    }
}