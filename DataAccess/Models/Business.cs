using DataAccess.Interfaces;
using LiteDB;

namespace DataAccess.Models
{
    public class Business
    {
        public ObjectId Id { get; set; } = new();

        public string Name { get; set; } = string.Empty;

        public IAddress? Address { get; set; }
    }
}