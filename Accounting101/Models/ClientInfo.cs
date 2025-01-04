using DataAccess.Interfaces;
using DataAccess.Models;

namespace Accounting101.Models
{
    public class ClientInfo
    {
        public string BusinessName { get; set; } = string.Empty;

        public PersonName? PersonName { get; set; }

        public IAddress? Address { get; set; }
    }
}
