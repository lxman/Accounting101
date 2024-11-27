using System;
using System.Collections.Generic;
using System.Text;
using DataAccess.Interfaces;

namespace DataAccess.Models
{
    public class UsAddress : IAddress
    {
        public Guid Id { get; set; }

        public List<Guid> UsedByIds { get; } = [];

        public string Line1 { get; set; }

        public string Line2 { get; set; }

        public string City { get; set; }

        public string State { get; set; }

        public string Country { get; set; } = "US";

        public string Zip { get; set; }

        public new string ToString()
        {
            StringBuilder toReturn = new();
            if (!string.IsNullOrEmpty(Line1))
            {
                toReturn.AppendLine(Line1);
            }
            if (!string.IsNullOrEmpty(Line2))
            {
                toReturn.AppendLine(Line2);
            }
            if (!string.IsNullOrEmpty(City))
            {
                toReturn.Append(City);
            }
            if (!string.IsNullOrEmpty(State))
            {
                toReturn.Append(", " + State);
            }
            if (!string.IsNullOrEmpty(Zip))
            {
                toReturn.Append(" " + Zip);
            }

            return toReturn.ToString();
        }
    }
}