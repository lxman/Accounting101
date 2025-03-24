using System;

namespace Accounting101.Angular.DataAccess.Models;
public class AccountGroupListItem
{
    public AccountGroupListItemType Type { get; set; }

    public Guid? AccountId { get; set; }

    public AccountGroup? AccountGroup { get; set; }
}
