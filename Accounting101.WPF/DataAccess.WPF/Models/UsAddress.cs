using System;
using System.Collections.Generic;
using System.Text;
using DataAccess.WPF.Interfaces;

namespace DataAccess.WPF.Models;

public class UsAddress : IAddress
{
    public Guid Id { get; set; }

    public List<Guid> UsedByIds { get; } = [];

    public string Line1 { get; set; } = string.Empty;

    public string Line2 { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Country { get; set; } = "US";

    public string Zip { get; set; } = string.Empty;

    public new string ToString()
    {
        StringBuilder toReturn = new();
        if (!string.IsNullOrWhiteSpace(Line1))
        {
            toReturn.AppendLine(Line1);
        }
        if (!string.IsNullOrWhiteSpace(Line2))
        {
            toReturn.AppendLine(Line2);
        }
        if (!string.IsNullOrWhiteSpace(City))
        {
            toReturn.Append(City);
        }
        if (!string.IsNullOrWhiteSpace(State))
        {
            toReturn.Append(", " + State);
        }
        if (!string.IsNullOrWhiteSpace(Zip))
        {
            toReturn.Append(" " + Zip);
        }

        toReturn.AppendLine();

        return toReturn.ToString();
    }
}