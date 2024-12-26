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
}