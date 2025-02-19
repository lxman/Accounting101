using System.Collections.Generic;
using MongoDB.Bson;

namespace DataAccess.Models
{
    public class CountryList
    {
        public ObjectId Id { get; set; }

        public List<string> Countries { get; set; } = [];
    }
}
