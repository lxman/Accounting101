using System.Collections.Generic;
using Accounting101.Angular.DataAccess.Models;

namespace Accounting101.Angular.DataAccess.CoATemplates;

public class ChartOfAccounts
{
    public CoANumberingBasis NumberingBasis { get; set; }

    public List<AccountWithInfo> Accounts { get; set; } = [];
}