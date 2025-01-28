using System;

namespace DataAccess.Models;

public class AccountInfo
{
    public Guid Id { get; set; }

    public string CoAId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}