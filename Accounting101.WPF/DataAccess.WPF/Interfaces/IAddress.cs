using System;
using System.Collections.Generic;

namespace DataAccess.WPF.Interfaces;

public interface IAddress
{
    public Guid Id { get; set; }

    public List<Guid> UsedByIds { get; }

    string Country { get; set; }

    string Line1 { get; set; }

    string Line2 { get; set; }

    string ToString();
}