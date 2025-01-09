namespace Accounting101.Models
{
    public class TransactionInfoLine(
        Guid id,
        DateOnly when,
        decimal? credit,
        decimal? debit,
        decimal balance,
        string otherAccountInfo)
    {
        public Guid Id { get; set; } = id;

        public DateOnly When { get; set; } = when;

        public decimal? Credit { get; set; } = credit;

        public decimal? Debit { get; set; } = debit;

        public decimal Balance { get; set; } = balance;

        public string OtherAccountInfo { get; set; } = otherAccountInfo;
    }
}
