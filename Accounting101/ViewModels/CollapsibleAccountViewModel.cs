using DataAccess.Models;

namespace Accounting101.ViewModels
{
    public class CollapsibleAccountViewModel(AccountWithInfo a, bool isCredit)
    {
        public string Header => isCredit ? "Credit Account" : "Debit Account";

        public AccountWithInfo Account { get; } = a;
    }
}