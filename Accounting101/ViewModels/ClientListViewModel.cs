﻿using System.Collections.ObjectModel;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class ClientListViewModel
    {
        public ReadOnlyObservableCollection<ClientWithInfo> Clients { get; }

        public ClientListViewModel(IDataStore dataStore)
        {
            IEnumerable<ClientWithInfo>? clients = dataStore.AllClientsWithInfos();
            Clients = clients is not null
                ? new ReadOnlyObservableCollection<ClientWithInfo>(new ObservableCollection<ClientWithInfo>(clients))
                : ReadOnlyObservableCollection<ClientWithInfo>.Empty;
        }
    }
}