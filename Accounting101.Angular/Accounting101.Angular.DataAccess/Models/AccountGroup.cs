using System;
using System.Collections.Generic;

namespace Accounting101.Angular.DataAccess.Models;

public class AccountGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public List<AccountGroup> Groups { get; set; } = [];

    public List<Guid> Accounts { get; set; } = [];
}