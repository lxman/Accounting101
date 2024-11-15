using System;

namespace DataAccess.Models
{
    public class Client
    {
        public Guid Id { get; set; }

        public string BusinessName { get; set; } = string.Empty;

        public Guid PersonNameId { get; set; }

        public Guid AddressId { get; set; }
    }
}