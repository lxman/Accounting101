namespace Accounting101.WPF.Models;

public class TransactionInfoLine(
    Guid id,
    DateOnly when,
    decimal? credit,
    decimal? debit,
    decimal balance,
    string otherAccountInfo,
    bool editable)
{
    public Guid Id { get; set; } = id;

    public DateOnly When { get; set; } = when;

    public decimal? Debit { get; set; } = debit;

    public decimal? Credit { get; set; } = credit;

    public decimal Balance { get; set; } = balance;

    public string OtherAccountInfo { get; set; } = otherAccountInfo;

    public bool Editable { get; set; } = editable;
}