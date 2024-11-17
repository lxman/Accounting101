using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace Accounting101.Views.Single
{
    /// <summary>
    /// Interaction logic for ClientView.xaml
    /// </summary>
    public partial class ClientView : UserControl
    {
        public ClientView(IDataStore dataStore, Guid clientId)
        {
            InitializeComponent();
            DataContext = new ClientViewModel(dataStore, dataStore.GetClientWithInfo(clientId) ?? new ClientWithInfo(dataStore, new Client()));
        }
    }
}
