using System.Collections.ObjectModel;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable VSTHRD002

namespace Accounting101.ViewModels
{
    public class ClientListViewModel : BaseViewModel
    {
        public ReadOnlyObservableCollection<ClientWithInfo> Clients { get; }

        public ClientListViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            IEnumerable<ClientWithInfo>? clients = taskFactory.Run(dataStore.AllClientsWithInfosAsync);
            Clients = clients is not null
                ? new ReadOnlyObservableCollection<ClientWithInfo>(new ObservableCollection<ClientWithInfo>(clients))
                : ReadOnlyObservableCollection<ClientWithInfo>.Empty;
        }
    }
}