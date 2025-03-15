namespace Accounting101.Angular.Server.Models;

public class CreateCoARequest
{
    public string Name { get; set; } = string.Empty;

    public string DbName { get; set; } = string.Empty;

    public Guid ClientId { get; set; }
}
