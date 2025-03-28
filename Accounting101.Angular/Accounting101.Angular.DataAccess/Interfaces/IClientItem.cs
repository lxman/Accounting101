using System;

namespace Accounting101.Angular.DataAccess.Interfaces;

public interface IClientItem
{
    public Guid Id { get; set; }

    public string ClientId { get; }
}