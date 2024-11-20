using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class USAddressViewModel
    {
        public UsAddress Address { get; set; }

        public USAddressViewModel(IDataStore dataStore, Guid id)
        {
            Address = (dataStore.FindAddressById(id) as UsAddress) ?? new UsAddress();
        }
    }
}
