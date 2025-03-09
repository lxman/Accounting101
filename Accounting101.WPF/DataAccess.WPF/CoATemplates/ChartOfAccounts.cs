using System.Collections.Generic;
using DataAccess.WPF.Models;

namespace DataAccess.WPF.CoATemplates;

public class ChartOfAccounts
{
    public CoANumberingBasis NumberingBasis { get; set; }

    public List<AccountWithInfo> Accounts { get; set; } = [];
}