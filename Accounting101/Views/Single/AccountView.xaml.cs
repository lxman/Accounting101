using System.Windows.Controls;
using DataAccess.Models;

namespace Accounting101.Views.Single
{
    public partial class AccountView : UserControl
    {
        public AccountView(AccountWithTransactions a)
        {
            InitializeComponent();
        }
    }
}