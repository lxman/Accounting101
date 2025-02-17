using System;
using System.Collections.Generic;

namespace DataAccess.Interfaces;

public interface IAddress : IModel
{
    List<Guid> UsedByIds { get; }

    string Country { get; set; }

    string Line1 { get; set; }

    string Line2 { get; set; }

    string ToString();
}