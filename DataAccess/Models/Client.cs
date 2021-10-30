using System;

namespace DataAccess.Models
{
    public class Client
    {
        public Guid Id { get; set; }

        public Guid Name { get; set; }

        public Guid Address { get; set; }
    }
}