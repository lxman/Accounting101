namespace Accounting101.WPF.Messages;

public class ShowAccountTransactionMessage
{
    public bool Value { get; set; }

    public Guid AccountId { get; set; }
}