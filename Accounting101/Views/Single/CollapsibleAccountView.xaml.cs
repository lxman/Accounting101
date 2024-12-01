using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Models;

namespace Accounting101.Views.Single
{
    public partial class CollapsibleAccountView : UserControl
    {
        public CollapsibleAccountView(AccountWithInfo a)
        {
            DataContext = new CollapsibleAccountViewModel(a);
            InitializeComponent();
        }
    }
}