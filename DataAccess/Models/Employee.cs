using System;
using System.Collections.Generic;

namespace DataAccess.Models
{
    public class Employee
    {
        public Guid Id { get; set; }

        public List<Guid> ClientIds { get; } = [];

        public Guid NameId { get; set; }

        public Guid AddressId { get; set; }
    }
}