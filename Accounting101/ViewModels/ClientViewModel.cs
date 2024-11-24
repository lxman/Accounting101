using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class ClientViewModel : BaseViewModel
    {
        public ClientWithInfo? Client { get; }

        public ClientViewModel(IDataStore dataStore, ClientWithInfo client)
        {
            Client = dataStore.GetClientWithInfo(client.Id);
        }
    }
}