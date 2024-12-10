using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    public partial class CreateTransactionView : UserControl
    {
        public CreateTransactionView(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            Guid clientId,
            Guid acctId)
        {
            DataContext = new CreateTransactionViewModel(dataStore, taskFactory, clientId, acctId);
            InitializeComponent();
        }
    }
}