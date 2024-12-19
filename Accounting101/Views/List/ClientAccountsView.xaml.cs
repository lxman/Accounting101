using System.Windows.Controls;
using System.Windows.Input;
using Accounting101.Messages;
using Accounting101.ViewModels.List;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.List
{
    public partial class ClientAccountsView : UserControl, IRecipient<BubbledScrollEventMessage>
    {
        public ClientAccountsView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            WeakReferenceMessenger.Default.Register(this);
            ClientAccountsViewModel vm = new(dataStore, taskFactory, clientId);
            DataContext = vm;
            InitializeComponent();
        }

        private void ClientMouseDown(object sender, MouseButtonEventArgs e)
        {
            WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.ClientList));
        }

        public void Receive(BubbledScrollEventMessage message)
        {
            if (message.Value.Delta > 0)
            {
                ScrollViewer.LineUp();
            }
            else
            {
                ScrollViewer.LineDown();
            }
        }
    }
}