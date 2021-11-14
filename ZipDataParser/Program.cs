using LiteDB;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZipDataParser
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            List<string> data = File.ReadAllLines("ziplist5.txt").ToList();
            List<Entry> entries = new();
            data.ForEach(d =>
            {
                string[] parts = d.Split(',');
                Entry e = new()
                {
                    City = parts[0],
                    State = parts[1],
                    Zip = parts[2],
                    FIPS = parts[3],
                    County = parts[4]
                };
                entries.Add(e);
            });
            LiteDatabase db = new("ZipInfo.db");
            ILiteCollection<Entry> zColl = db.GetCollection<Entry>("ZipInfo");
            zColl.InsertBulk(entries);
            db.Dispose();
        }
    }
}