using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Accounting101.Controls;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Read
{
    public partial class ClientListView : UserControl
    {
        public ReadOnlyObservableCollection<ClientTileControl> ClientTiles { get; }

        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;

        public ClientListView(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            List<ClientWithInfo> clients = _taskFactory.Run(() => _dataStore.AllClientsWithInfosAsync())?.ToList() ?? [];
            DataContext = this;
            ObservableCollection<ClientTileControl> clientTiles = [];
            clients.ForEach(c => clientTiles.Add(new ClientTileControl(c)));
            ClientTiles = new ReadOnlyObservableCollection<ClientTileControl>(clientTiles);
            InitializeComponent();
        }

        private void ClientListViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                foreach (ClientTileControl control in ClientTiles)
                {
                    control.Width = e.NewSize.Width * 0.9;
                }
            }
        }
    }
}