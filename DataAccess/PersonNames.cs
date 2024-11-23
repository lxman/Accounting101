using System;
using System.Collections.Generic;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace DataAccess
{
    public static class PersonNames
    {
        public static Guid CreateName(this IDataStore store, PersonName name)
        {
            Guid result = store.GetCollection<PersonName>(CollectionNames.PersonName)?.Insert(name).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(PersonNames));
            return result;
        }

        public static void BulkInsertNames(this IDataStore store, IEnumerable<PersonName> names)
        {
            int? result = store.GetCollection<PersonName>(CollectionNames.PersonName)?.InsertBulk(names);
            if (result > 0) store.NotifyChanged(typeof(PersonNames));
        }

        public static IEnumerable<PersonName>? AllNames(IDataStore store)
        {
            return store.GetCollection<PersonName>(CollectionNames.PersonName)?.FindAll();
        }

        public static PersonName? FindNameById(this IDataStore store, Guid id)
        {
            return store.GetCollection<PersonName>(CollectionNames.PersonName)?.FindById(id);
        }
    }
}