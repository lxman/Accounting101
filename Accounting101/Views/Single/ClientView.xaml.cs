using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable VSTHRD002

namespace Accounting101.Views.Single
{
    public partial class ClientView : UserControl
    {
        public event EventHandler<Guid>? ClientChosen;

        public ClientWithInfo Client { get; }

        public string Contact => Client.Name?.ToString() ?? string.Empty;

        public string Address => Client.Address?.ToString() ?? string.Empty;

        public ClientView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            DataContext = this;
            Client = taskFactory.Run(() => dataStore.GetClientWithInfoAsync(clientId)) ?? new ClientWithInfo(dataStore, new Client());
            InitializeComponent();
        }

        public ClientView(IDataStore dataStore, JoinableTaskFactory taskFactory, Client client)
        {
            DataContext = this;
            Client = new ClientWithInfo(dataStore, client);
            InitializeComponent();
        }

        private void OnFocused()
        {
            ClientItem.Background = new SolidColorBrush(Colors.LightGray);
        }

        private void OnUnfocused()
        {
            ClientItem.Background = new SolidColorBrush(Colors.White);
        }

        private void StackPanelOnMouseEnter(object sender, MouseEventArgs e)
        {
            OnFocused();
        }

        private void StackPanelOnMouseLeave(object sender, MouseEventArgs e)
        {
            OnUnfocused();
        }

        private void StackPanelOnGotFocus(object sender, RoutedEventArgs e)
        {
            OnFocused();
        }

        private void StackPanelOnLostFocus(object sender, RoutedEventArgs e)
        {
            OnUnfocused();
        }

        private void ClientOnMouseDown(object sender, MouseButtonEventArgs e)
        {
            ClientChosen?.Invoke(this, Client.Id);
        }

        private void ClientItemPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                ClientChosen?.Invoke(this, Client.Id);
            }
        }
    }
}