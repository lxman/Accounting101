using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Single
{
    public partial class TransactionView : UserControl
    {
        public TransactionView(IDataStore dataStore, JoinableTaskFactory taskFactory, Transaction t, Guid relativeAccountId)
        {
            DataContext = new TransactionViewModel(dataStore, taskFactory, t, relativeAccountId);
            InitializeComponent();
        }
    }
}