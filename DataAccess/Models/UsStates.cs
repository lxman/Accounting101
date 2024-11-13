using System.Collections.Generic;
using System.Linq;
using DataAccess.Data;
using LiteDB;

namespace DataAccess.Models
{
    public class UsStates
    {
        public List<string> States { get; }

        public UsStates()
        {
            LiteDatabase db = new(@"Data\ZipInfo.db");
            ILiteCollection<Entry> entries = db.GetCollection<Entry>("ZipInfo");
            States = entries.FindAll().Select(e => e.State).Distinct().ToList();
            db.Dispose();
        }
    }
}