using System;
using System.Collections.Generic;

namespace DataAccess.Models
{
    public class Employee
    {
        public Guid Id { get; set; }

        public List<Guid> Clients { get; } = new();

        public Guid Name { get; set; }

        public Guid Address { get; set; }
    }
}