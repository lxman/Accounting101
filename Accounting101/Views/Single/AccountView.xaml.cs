using System.Windows.Controls;
using Accounting101.Models;
using Accounting101.ViewModels;
using DataAccess.Models;

namespace Accounting101.Views.Single
{
    public partial class AccountView : UserControl
    {
        public AccountView(AccountWithTransactions a, AccountWithInfoFlat f, AccountWithInfo awi)
        {
            DataContext = new AccountViewModel(a, f, awi);
            InitializeComponent();
        }
    }
}