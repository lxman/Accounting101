using System;

namespace DataAccess.WPF.Models;

public class AccountInfo
{
    public Guid Id { get; set; }

    public string CoAId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}