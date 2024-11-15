using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class ClientControlViewModel
    {
        public ClientWithInfo? Client { get; }

        public ClientControlViewModel(IDataStore dataStore, ClientWithInfo client)
        {
            Client = dataStore.GetClientWithInfo(client.Id);
        }
    }
}