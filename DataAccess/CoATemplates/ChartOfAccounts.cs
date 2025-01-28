using System.Collections.Generic;
using DataAccess.Models;

namespace DataAccess.CoATemplates;

public class ChartOfAccounts
{
    public CoANumberingBasis NumberingBasis { get; set; }

    public List<AccountWithInfo> Accounts { get; set; } = [];
}