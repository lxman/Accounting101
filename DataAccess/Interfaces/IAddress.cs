using System;

namespace DataAccess.Interfaces
{
    public interface IAddress
    {
        Guid Id { get; set; }
        string Country { get; set; }
        string Line1 { get; set; }
        string Line2 { get; set; }
    }
}