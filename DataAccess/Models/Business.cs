using DataAccess.Interfaces;

namespace DataAccess.Models
{
    public class Business
    {
        public string Name { get; set; }

        public IAddress Address { get; set; }
    }
}