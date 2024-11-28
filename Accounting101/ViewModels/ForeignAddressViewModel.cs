using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class ForeignAddressViewModel : BaseViewModel
    {
        public ForeignAddress Address { get; set; }

        public ForeignAddressViewModel(IDataStore dataStore, Guid? id = null)
        {
            if (id.HasValue)
            {
                Address = (dataStore.FindAddressByIdAsync(id.Value).GetAwaiter().GetResult() as ForeignAddress) ?? new ForeignAddress();
            }
            else
            {
                Address = new ForeignAddress();
            }
        }
    }
}
