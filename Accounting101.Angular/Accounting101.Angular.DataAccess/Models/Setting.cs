using System;
using Accounting101.Angular.DataAccess.Interfaces;

namespace Accounting101.Angular.DataAccess.Models;

public class Setting : IGlobalItem
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}