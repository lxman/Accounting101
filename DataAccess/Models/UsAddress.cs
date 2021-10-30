using DataAccess.Interfaces;
using System;

namespace DataAccess.Models
{
    public class UsAddress : IAddress
    {
        public Guid Id { get; set; }

        public string Line1 { get; set; }

        public string Line2 { get; set; }

        public string City { get; set; }

        public string State { get; set; }

        public string Country { get; set; } = "US";

        public string Zip { get; set; }
    }
}