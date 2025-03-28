namespace Accounting101.Angular.DataAccess.Models;
public class AccountGroupListItem
{
    public AccountGroupListItemType Type { get; set; }

    public string? AccountId { get; set; }

    public AccountGroup? AccountGroup { get; set; }
}
