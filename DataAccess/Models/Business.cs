using DataAccess.Interfaces;
using LiteDB;

namespace DataAccess.Models
{
    public class Business
    {
        public ObjectId Id { get; set; }

        public string Name { get; set; }

        public IAddress Address { get; set; }
    }
}