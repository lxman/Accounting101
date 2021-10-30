using System;
using System.Collections.Generic;

namespace DataAccess.Models
{
    public class PersonName
    {
        public Guid Id { get; set; }

        public List<Guid> UsedBy { get; } = new();

        public string Prefix { get; set; }

        public string First { get; set; }

        public string Middle { get; set; }

        public string Last { get; set; }

        public string Suffix { get; set; }
    }
}