using DataAccess.Interfaces;

namespace DataAccess
{
    public class Business
    {
        public string Name { get; set; }

        public IAddress Address { get; set; }
    }
}
