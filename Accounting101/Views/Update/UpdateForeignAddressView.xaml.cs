using System.Windows.Controls;
using Accounting101.ViewModels.Single;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Update
{
    public partial class UpdateForeignAddressView : UserControl
    {
        public UpdateForeignAddressView(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            Guid addressId)
        {
            DataContext = new ForeignAddressViewModel(dataStore, taskFactory, addressId);
            InitializeComponent();
        }
    }
}