using System;
using System.Collections.Generic;
using Accounting101.Angular.DataAccess.Interfaces;

namespace Accounting101.Angular.DataAccess.Models;

public class PersonName : IGlobalItem
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public List<Guid> UsedByIds { get; } = [];

    public string Prefix { get; set; } = string.Empty;

    public string First { get; set; } = string.Empty;

    public string Middle { get; set; } = string.Empty;

    public string Last { get; set; } = string.Empty;

    public string Suffix { get; set; } = string.Empty;

    public new string ToString()
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(Prefix))
        {
            parts.Add(Prefix);
        }
        if (!string.IsNullOrWhiteSpace(First))
        {
            parts.Add(First);
        }
        if (!string.IsNullOrWhiteSpace(Middle))
        {
            parts.Add(Middle);
        }
        if (!string.IsNullOrWhiteSpace(Last))
        {
            parts.Add(Last);
        }
        if (!string.IsNullOrWhiteSpace(Suffix))
        {
            parts.Add(Suffix);
        }
        return string.Join(' ', parts);
    }
}