using DataAccess.Interfaces;
using System;

namespace DataAccess.Models
{
    public class ForeignAddress : IAddress
    {
        public Guid Id { get; set; }
        public string Country { get; set; }
        public string Line1 { get; set; }
        public string Line2 { get; set; }
        public string Province { get; set; }
        public string PostalCode { get; set; }
    }
}
