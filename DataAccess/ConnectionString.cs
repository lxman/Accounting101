using System;
using System.IO;

#pragma warning disable CA2211

namespace DataAccess
{
    public static class ConnectionString
    {
        public static string ConnString = $"FileName={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Accounts.db")};Password=1234;";
        public static readonly string RoConnString = $"{ConnString}ReadOnly=true;";
    }
}