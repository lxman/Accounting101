using System;
using System.Collections.Generic;
using System.Text;
using Accounting101.Angular.DataAccess.Interfaces;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Angular.DataAccess.Models;

[BsonDiscriminator(nameof(ForeignAddress))]
public class ForeignAddress : IAddress
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public List<Guid> UsedByIds { get; } = [];

    public string Country { get; set; } = string.Empty;

    public string Line1 { get; set; } = string.Empty;

    public string Line2 { get; set; } = string.Empty;

    public string Province { get; set; } = string.Empty;

    public string PostalCode { get; set; } = string.Empty;

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
        switch (string.IsNullOrWhiteSpace(Province))
        {
            case false when !string.IsNullOrWhiteSpace(PostalCode):
                toReturn.AppendLine(Province + " " + PostalCode);
                break;

            case false:
                toReturn.AppendLine(Province);
                break;

            default:
                {
                    if (!string.IsNullOrWhiteSpace(PostalCode))
                    {
                        toReturn.AppendLine(PostalCode);
                    }

                    break;
                }
        }
        if (!string.IsNullOrWhiteSpace(Country))
        {
            toReturn.AppendLine(Country);
        }
        return toReturn.ToString();
    }
}