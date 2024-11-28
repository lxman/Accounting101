using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
#pragma warning disable VSTHRD002

namespace Accounting101.Views.Single
{
    /// <summary>
    /// Interaction logic for ClientView.xaml
    /// </summary>
    public partial class ClientView : UserControl
    {
        public event EventHandler<Guid>? ClientChosen;

        public ClientWithInfo Client { get; }

        public string Contact => Client.Name?.ToString() ?? string.Empty;

        public string Address => Client.Address?.ToString() ?? string.Empty;

        public ClientView(IDataStore dataStore, Guid clientId)
        {
            DataContext = this;
            Client = dataStore.GetClientWithInfoAsync(clientId).GetAwaiter().GetResult() ?? new ClientWithInfo(dataStore, new Client());
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
    }
}
