using DataAccess.WPF.Interfaces;
using DataAccess.WPF.Models;

namespace Accounting101.WPF.Models;

public class ClientInfo
{
    public Guid Id { get; set; }

    public Guid PersonNameId { get; set; }

    public Guid AddressId { get; set; }

    public string BusinessName { get; set; } = string.Empty;

    public PersonName? PersonName { get; set; }

    public IAddress? Address { get; set; }
}