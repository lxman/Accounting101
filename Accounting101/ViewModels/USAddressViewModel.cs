using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class USAddressViewModel : BaseViewModel
    {
        public UsAddress Address { get; set; }

        public List<object> States { get; }

        public USAddressViewModel(IDataStore dataStore, Guid? id = null)
        {
            if (id.HasValue)
            {
                Address = (dataStore.FindAddressByIdAsync(id.Value).GetAwaiter().GetResult() as UsAddress) ?? new UsAddress();
            }
            else
            {
                Address = new UsAddress();
            }
            States = dataStore.GetStatesAsync().GetAwaiter().GetResult().Order().Cast<object>().ToList();
        }
    }
}
