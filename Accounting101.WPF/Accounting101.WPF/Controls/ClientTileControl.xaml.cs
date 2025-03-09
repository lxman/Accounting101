using System.Windows;
using Accounting101.WPF.Messages;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF.Models;

namespace Accounting101.WPF.Controls;

public partial class ClientTileControl
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