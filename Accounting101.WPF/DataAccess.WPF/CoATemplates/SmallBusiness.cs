﻿using System.Collections.Generic;
using DataAccess.WPF.Models;

namespace DataAccess.WPF.CoATemplates;

public class SmallBusiness
{
    public static string Description => "A simple chart of accounts appropriate for a small business.";

    public static ChartOfAccounts CreateCoA(Client c)
    {
        List<AccountWithInfo> accounts =
        [
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Asset, StartBalance = 0 }, new AccountInfo { Name = "Cash", CoAId = "101" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Asset, StartBalance = 0 }, new AccountInfo { Name = "Accounts Receivable", CoAId = "120" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Asset, StartBalance = 0 }, new AccountInfo { Name = "Merchandise Inventory", CoAId = "140" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Asset, StartBalance = 0 }, new AccountInfo { Name = "Supplies", CoAId = "150" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Asset, StartBalance = 0 }, new AccountInfo { Name = "Prepaid Insurance", CoAId = "160" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Asset, StartBalance = 0 }, new AccountInfo { Name = "Land", CoAId = "170" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Asset, StartBalance = 0 }, new AccountInfo { Name = "Buildings", CoAId = "175" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Asset, StartBalance = 0 }, new AccountInfo { Name = "Equipment", CoAId = "180" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Liability, StartBalance = 0 }, new AccountInfo { Name = "Notes Payable", CoAId = "210" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Liability, StartBalance = 0 }, new AccountInfo { Name = "Accounts Payable", CoAId = "215" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Liability, StartBalance = 0 }, new AccountInfo { Name = "Wages Payable", CoAId = "220" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Liability, StartBalance = 0 }, new AccountInfo { Name = "Interest Payable", CoAId = "230" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Liability, StartBalance = 0 }, new AccountInfo { Name = "Unearned Revenue", CoAId = "240" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Liability, StartBalance = 0 }, new AccountInfo { Name = "Mortgage Loan Payable", CoAId = "250" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Equity, StartBalance = 0 }, new AccountInfo { Name = "Owner's Capital", CoAId = "290" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Equity, StartBalance = 0 }, new AccountInfo { Name = "Owner's Drawings", CoAId = "295" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Revenue, StartBalance = 0 }, new AccountInfo { Name = "Sales", CoAId = "310" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Expense, StartBalance = 0 }, new AccountInfo { Name = "Salaries Expense", CoAId = "500" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Expense, StartBalance = 0 }, new AccountInfo { Name = "Wages Expense", CoAId = "510" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Expense, StartBalance = 0 }, new AccountInfo { Name = "Supplies Expense", CoAId = "540" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Expense, StartBalance = 0 }, new AccountInfo { Name = "Rent Expense", CoAId = "560" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Expense, StartBalance = 0 }, new AccountInfo { Name = "Utilities Expense", CoAId = "570" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Expense, StartBalance = 0 }, new AccountInfo { Name = "Telephone Expense", CoAId = "576" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Expense, StartBalance = 0 }, new AccountInfo { Name = "Advertising Expense", CoAId = "610" }),
            new(new Account { ClientId = c.Id, Type = BaseAccountTypes.Expense, StartBalance = 0 }, new AccountInfo { Name = "Depreciation Expense", CoAId = "750" })
        ];
        return new ChartOfAccounts { NumberingBasis = CoANumberingBasis.Hundreds, Accounts = accounts };
    }
}