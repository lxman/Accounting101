using System;
using Accounting101.Angular.DataAccess.Interfaces;

namespace Accounting101.Angular.DataAccess.Models;

public class AccountInfo : IModel
{
    public Guid Id { get; set; }

    public string CoAId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}