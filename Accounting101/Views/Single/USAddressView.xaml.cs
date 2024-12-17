using System.Windows.Controls;
using Accounting101.ViewModels.Single;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Single
{
    public partial class USAddressView : UserControl
    {
        public USAddressView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid id)
        {
            DataContext = new USAddressViewModel(dataStore, taskFactory, id);
            InitializeComponent();
        }
    }
}