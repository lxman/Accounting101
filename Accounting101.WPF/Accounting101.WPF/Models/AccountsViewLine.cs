namespace Accounting101.WPF.Models;

public class AccountsViewLine
{
    public Guid Id { get; set; }

    public DateOnly Created { get; set; }

    public string CoAId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public BaseAccountTypes Type { get; set; }

    public decimal StartBalance { get; set; }

    public decimal CurrentBalance { get; set; }
}