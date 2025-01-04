using System.Windows;
using Accounting101.Messages;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.Models;
using MahApps.Metro.Controls;

namespace Accounting101.Controls
{
    public partial class ClientTileControl : Tile
    {
        public ClientWithInfo Client { get; }

        public ClientTileControl(ClientWithInfo client)
        {
            Client = client;
            InitializeComponent();
        }

        private void ClientTileControlClick(object sender, RoutedEventArgs e)
        {
            WeakReferenceMessenger.Default.Send(new FocusClientMessage(Client));
        }
    }
}
