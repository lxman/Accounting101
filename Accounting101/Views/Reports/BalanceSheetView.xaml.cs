using System.Windows.Controls;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Reports
{
    /// <summary>
    /// Interaction logic for BalanceSheetView.xaml
    /// </summary>
    public partial class BalanceSheetView : UserControl
    {
        public string AsOfDate { get; }

        public BalanceSheetView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            Business business = taskFactory.Run(dataStore.GetBusinessAsync)!;
            Client client = taskFactory.Run(() => dataStore.FindClientByIdAsync(clientId))!;
            AsOfDate = $"As Of {DateTime.Now:MM/dd/yyyy}";
            DataContext = this;
            InitializeComponent();
            BusinessInfo.SetBusiness(business);
            ClientInfo.SetClient(dataStore, taskFactory, client);
        }
    }
}
