using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    public partial class CreateUSAddressView : UserControl
    {
        public CreateUSAddressView(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            DataContext = new USAddressViewModel(dataStore, taskFactory);
            InitializeComponent();
        }
    }
}
