using System.Collections.ObjectModel;
using System.Windows;
using Accounting101.WPF.Controls;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.WPF.Views.Read;

public partial class ClientListView
{
    public ReadOnlyObservableCollection<ClientTileControl> ClientTiles { get; }

    public ClientListView(IDataStore dataStore, JoinableTaskFactory taskFactory)
    {
        IDataStore dataStore1 = dataStore;
        List<ClientWithInfo> clients = taskFactory.Run(() => dataStore1.AllClientsWithInfosAsync())?.ToList() ?? [];
        DataContext = this;
        ObservableCollection<ClientTileControl> clientTiles = [];
        clients.ForEach(c => clientTiles.Add(new ClientTileControl(c)));
        ClientTiles = new ReadOnlyObservableCollection<ClientTileControl>(clientTiles);
        InitializeComponent();
    }

    private void ClientListViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
        {
            return;
        }

        foreach (ClientTileControl control in ClientTiles)
        {
            control.Width = e.NewSize.Width * 0.9;
        }
    }
}