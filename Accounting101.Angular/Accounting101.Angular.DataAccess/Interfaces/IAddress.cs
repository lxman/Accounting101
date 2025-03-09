using System;
using System.Collections.Generic;

namespace Accounting101.Angular.DataAccess.Interfaces;

public interface IAddress
{
    Guid Id { get; set; }

    List<Guid> UsedByIds { get; }

    string Country { get; set; }

    string Line1 { get; set; }

    string Line2 { get; set; }

    string ToString();
}