namespace Accounting101.Angular.DataAccess.Models;

public static class CollectionNames
{
    public const string Account = nameof(Account);
    public const string AccountCheckpoint = nameof(AccountCheckpoint);
    public const string AccountInfo = nameof(AccountInfo);
    public const string Address = "IAddress";
    public const string AuditEntry = nameof(AuditEntry);
    public const string Business = nameof(Business);
    public const string CheckPoint = nameof(CheckPoint);
    public const string Client = nameof(Client);
    public const string CountryInfo = nameof(CountryInfo);
    public const string PersonName = nameof(PersonName);
    public const string RootGroup = nameof(RootGroup);
    public const string Setting = nameof(Setting);
    public const string Transaction = nameof(Transaction);
    public const string ZipInfo = nameof(ZipInfo);

    public static string GetCollectionName<T>() => typeof(T).Name;
}