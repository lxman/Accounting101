using System;
using System.Collections.Generic;

namespace Accounting101.Angular.DataAccess.Models;

public class AccountGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public List<AccountGroupListItem> Items { get; set; } = [];
}