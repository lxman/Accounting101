using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;

// ReSharper disable StringLiteralTypo

namespace ZipDataParser.WPF;

internal class Program
{
    private static void Main(string[] args)
    {
        List<string> data = File.ReadAllLines("ziplist5.txt").ToList();
        List<ZipCodeEntry> entries = [];
        data.ForEach(d =>
        {
            string[] parts = d.Split(',');
            ZipCodeEntry e = new()
            {
                City = parts[0],
                State = parts[1],
                Zip = parts[2],
                AreaCode = parts[3],
                Fips = parts[4],
                County = parts[5]
            };
            entries.Add(e);
        });
        LiteDatabase db = new("ZipInfo.db");
        ILiteCollection<ZipCodeEntry> zColl = db.GetCollection<ZipCodeEntry>("ZipInfo");
        zColl.InsertBulk(entries);
        db.Dispose();
    }
}