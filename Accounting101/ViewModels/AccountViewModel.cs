using Accounting101.Controls;
using Accounting101.Models;

namespace Accounting101.ViewModels
{
    public class AccountViewModel : BaseViewModel
    {
        public AccountHeaderControl AccountHeaderControl { get; }

        //public ObservableCollection<>

        public AccountViewModel(AccountWithInfoFlat a)
        {
            AccountHeaderControl = new AccountHeaderControl(a);
        }
    }
}