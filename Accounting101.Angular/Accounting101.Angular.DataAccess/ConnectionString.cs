#pragma warning disable CA2211

namespace Accounting101.Angular.DataAccess;

public static class ConnectionString
{
    public static string ConnString = string.Empty;
    public static readonly string RoConnString = $"{ConnString}ReadOnly=true;";
}