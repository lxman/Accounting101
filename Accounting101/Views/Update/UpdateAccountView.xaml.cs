using System.Windows.Controls;
using Accounting101.ViewModels.Update;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Update
{
    public partial class UpdateAccountView : UserControl
    {
        private readonly UpdateAccountViewModel _vm = new();

        public UpdateAccountView()
        {
            DataContext = _vm;
            InitializeComponent();
        }

        public void SetAccount(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid accountId)
        {
            _vm.SetAccount(dataStore, taskFactory, accountId);
        }
    }
}