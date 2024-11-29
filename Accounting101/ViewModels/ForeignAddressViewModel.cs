using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels
{
    public class ForeignAddressViewModel : BaseViewModel
    {
        public ForeignAddress Address { get; set; }

        public ForeignAddressViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid? id = null)
        {
            if (id.HasValue)
            {
                Address = (taskFactory.Run(() => dataStore.FindAddressByIdAsync(id.Value)) as ForeignAddress) ?? new ForeignAddress();
            }
            else
            {
                Address = new ForeignAddress();
            }
        }
    }
}