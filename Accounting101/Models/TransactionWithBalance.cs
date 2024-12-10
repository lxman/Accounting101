using DataAccess.Models;

namespace Accounting101.Models
{
    public class TransactionWithBalance
    {
        public Transaction Transaction { get; set; }

        public decimal Balance { get; set; }
    }
}