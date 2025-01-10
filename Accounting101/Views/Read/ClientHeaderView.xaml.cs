using System.Windows.Controls;
using System.Windows.Input;
using Accounting101.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.Models;

namespace Accounting101.Views.Read
{
    [ObservableObject]
    public partial class ClientHeaderView : UserControl
    {
        public string BusinessName { get; private set; } = string.Empty;

        public string Contact { get; private set; } = string.Empty;

        public string Address { get; private set; } = string.Empty;

        public ClientHeaderView()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetInfo(ClientWithInfo client)
        {
            BusinessName = client.BusinessName;
            Contact = client.Name?.ToString() ?? string.Empty;
            Address = client.Address?.ToString() ?? string.Empty;
            OnPropertyChanged(nameof(BusinessName));
            OnPropertyChanged(nameof(Contact));
            OnPropertyChanged(nameof(Address));
        }

        private void ClientHeaderViewPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.ClientList));
        }
    }
}