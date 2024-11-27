using System;
using System.Collections.Generic;
using System.Text;
using DataAccess.Interfaces;

namespace DataAccess.Models
{
    public class ForeignAddress : IAddress
    {
        public Guid Id { get; set; }

        public List<Guid> UsedByIds { get; } = [];

        public string Country { get; set; }

        public string Line1 { get; set; }

        public string Line2 { get; set; }

        public string Province { get; set; }

        public string PostalCode { get; set; }

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
            if (!string.IsNullOrEmpty(Province))
            {
                toReturn.Append(Province);
            }
            if (!string.IsNullOrEmpty(PostalCode))
            {
                toReturn.Append(" " + PostalCode);
            }
            return toReturn.ToString();
        }
    }
}