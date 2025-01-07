using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels.Update
{
    public class UpdateAccountEntriesViewModel : BaseViewModel
    {
        public UpdateAccountEntriesViewModel()
        {

        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client, AccountWithTransactions account)
        {
        }
    }
}