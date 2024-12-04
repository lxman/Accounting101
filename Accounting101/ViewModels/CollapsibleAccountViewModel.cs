using DataAccess.Models;

namespace Accounting101.ViewModels
{
    public class CollapsibleAccountViewModel(AccountWithInfo a, bool isCredited)
    {
        public string Header => !isCredited ? "Debited Account" : "Credited Account";

        public AccountWithInfo Account { get; } = a;
    }
}