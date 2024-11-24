using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class USAddressViewModel : BaseViewModel
    {
        public UsAddress Address { get; set; }

        public USAddressViewModel(IDataStore dataStore, Guid? id = null)
        {
            if (id.HasValue)
            {
                Address = (dataStore.FindAddressById(id.Value) as UsAddress) ?? new UsAddress();
            }
            else
            {
                Address = new UsAddress();
            }
        }
    }
}
