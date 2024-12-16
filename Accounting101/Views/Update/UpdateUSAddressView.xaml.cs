using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Update
{
    public partial class UpdateUSAddressView : UserControl
    {
        public UpdateUSAddressView(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            Guid addressId)
        {
            DataContext = new USAddressViewModel(dataStore, taskFactory, addressId);
            InitializeComponent();
        }
    }
}