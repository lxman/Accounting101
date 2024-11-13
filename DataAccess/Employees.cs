using System;
using System.Collections.Generic;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace DataAccess
{
    public static class Employees
    {
        public static Guid CreateEmployee(this IDataStore store, Employee e)
        {
            Guid result = store.GetCollection<Employee>(CollectionNames.Employees)?.Insert(e).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Employees));
            return result;
        }

        public static void BulkInsertEmployees(this IDataStore store, IEnumerable<Employee> employees)
        {
            int? result = store.GetCollection<Employee>(CollectionNames.Employees)?.InsertBulk(employees);
            if (result > 0) store.NotifyChanged(typeof(Employees));
        }

        public static IEnumerable<Employee>? AllEmployees(IDataStore store)
        {
            return store.GetCollection<Employee>(CollectionNames.Employees)?.FindAll();
        }

        public static Employee? FindEmployeeById(this IDataStore store, Guid id)
        {
            return store.GetCollection<Employee>(CollectionNames.Employees)?.FindById(id);
        }

        public static IEnumerable<Employee> EmployeesForClient(this IDataStore store, Guid clientId)
        {
            return store.GetCollection<Employee>(CollectionNames.Employees)?.Find(e => e.ClientIds.Contains(clientId)) ??
                   new List<Employee>();
        }
    }
}