using System;
using DataAccess.Interfaces;

namespace DataAccess.Models;

public class AccountInfo : IModel
{
    public Guid Id { get; set; }

    public string CoAId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}