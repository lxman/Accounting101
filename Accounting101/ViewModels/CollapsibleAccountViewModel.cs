using DataAccess.Models;

namespace Accounting101.ViewModels
{
    public class CollapsibleAccountViewModel(AccountWithInfo a)
    {
        public string Header => !Account.IsDebitAccount ? "Credit Account" : "Debit Account";

        public AccountWithInfo Account { get; } = a;
    }
}