using System;

namespace Accounting101.Angular.DataAccess.Models
{
    public class Customer
    {
        public Guid Id { get; set; }

        public string UserName { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;
    }
}
