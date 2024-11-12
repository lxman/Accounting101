using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
// ReSharper disable StringLiteralTypo

namespace ZipDataParser
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            List<string> data = File.ReadAllLines("ziplist5.txt").ToList();
            List<Entry> entries = [];
            data.ForEach(d =>
            {
                string[] parts = d.Split(',');
                Entry e = new()
                {
                    City = parts[0],
                    State = parts[1],
                    Zip = parts[2],
                    Fips = parts[3],
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