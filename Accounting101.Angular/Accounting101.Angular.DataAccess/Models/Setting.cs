using System;
using Accounting101.Angular.DataAccess.Interfaces;

namespace Accounting101.Angular.DataAccess.Models;

public class Setting : IModel
{
    public Guid Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}