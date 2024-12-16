using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels.Single
{
    public class USAddressViewModel : BaseViewModel
    {
        public UsAddress Address { get; set; }

        public List<object> States { get; }

        public USAddressViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid? id = null)
        {
            if (id.HasValue)
            {
                Address = taskFactory.Run(() => dataStore.FindAddressByIdAsync(id.Value)) as UsAddress ?? new UsAddress();
            }
            else
            {
                Address = new UsAddress();
            }
            States = taskFactory.Run(dataStore.GetStatesAsync).Order().Cast<object>().ToList();
        }
    }
}