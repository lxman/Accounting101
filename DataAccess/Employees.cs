using DataAccess.Models;
using DataAccess.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace DataAccess
{
    public static class Employees
    {
        public static Guid Create(this IDataStore store, Employee e)
        {
            return store.GetCollection<Employee>(CollectionNames.Employees)?.Insert(e).AsGuid ?? Guid.Empty;
        }

        public static void BulkInsert(this IDataStore store, IEnumerable<Employee> employees)
        {
            store.GetCollection<Employee>(CollectionNames.Employees)?.InsertBulk(employees);
        }

        public static Employee? FindById(this IDataStore store, Guid id)
        {
            return store.GetCollection<Employee>(CollectionNames.Employees)?.FindById(id);
        }

        public static IEnumerable<Employee> ForClient(this IDataStore store, Guid clientId)
        {
            return store.GetCollection<Employee>(CollectionNames.Employees)?.Find(e => e.Clients.Contains(clientId)) ??
                   new List<Employee>();
        }
    }
}