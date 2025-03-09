using System;
using System.Collections.Generic;

namespace DataAccess.WPF.Models;

public class PersonName
{
    public Guid Id { get; set; }

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