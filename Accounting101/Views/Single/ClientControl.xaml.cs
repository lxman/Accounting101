using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace Accounting101.Views.Single
{
    /// <summary>
    /// Interaction logic for ClientControl.xaml
    /// </summary>
    public partial class ClientControl : UserControl
    {
        public ClientControl(IDataStore dataStore, Guid clientId)
        {
            InitializeComponent();
            DataContext = new ClientControlViewModel(dataStore, dataStore.GetClientWithInfo(clientId) ?? new ClientWithInfo(dataStore, new Client()));
        }
    }
}
